using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace ProtocolCheck.Explore;

/// <summary>
/// A record of a model's state: fields of plain values (numbers, booleans, enums, strings, nullables), immutable
/// sequences (<see cref="Seq{T}"/>), messages (<see cref="Msg"/>) and other records. A copy copies the records inside
/// it and shares everything else, so a record is changed in place but a sequence or a message is only ever replaced.
/// Records that are held in a sequence are shared between copies too, so they must not be changed either.
/// </summary>
public abstract class Rec {
    /// <summary>
    /// A copy of the record, with copies of the records inside it.
    /// </summary>
    public T Clone<T>() where T : Rec => (T) Cloner.Clone(this);

    internal object ShallowCopy() => MemberwiseClone();

    public override string ToString() => string.Join(", ", Fields.Flatten(this).Select(pair => $"{pair.Name}={pair.Value}"));
}

/// <summary>
/// A message between the two games. Messages are immutable and compare by their fields.
/// </summary>
public abstract record Msg {
    /// <summary>
    /// The client update packet ID that the message travels under, which decides the order that a game handles the
    /// messages of one packet in.
    /// </summary>
    public abstract int Packet { get; }

    /// <summary>
    /// What the message is kept once per packet for, or null when every one is kept. A later message of the same kind
    /// for the same slot replaces an earlier one that is still in the packet being filled.
    /// </summary>
    public virtual object? Slot => null;
}

/// <summary>
/// Something that adds itself to a hash in its own way.
/// </summary>
public interface IWalkable {
    void Walk(Hasher hasher);
}

/// <summary>
/// An immutable sequence that compares and hashes by its items.
/// </summary>
public sealed class Seq<T> : IWalkable, IReadOnlyList<T> {
    public static readonly Seq<T> Empty = new([]);

    private readonly T[] _items;

    private Seq(T[] items) {
        _items = items;
    }

    public static Seq<T> Of(params T[] items) => items.Length == 0 ? Empty : new Seq<T>((T[]) items.Clone());

    public int Count => _items.Length;

    public T this[int index] => _items[index];

    public Seq<T> Add(T item) {
        var items = new T[_items.Length + 1];
        _items.CopyTo(items, 0);
        items[^1] = item;
        return new Seq<T>(items);
    }

    public Seq<T> AddRange(Seq<T> other) {
        if (other.Count == 0) {
            return this;
        }

        if (Count == 0) {
            return other;
        }

        var items = new T[_items.Length + other._items.Length];
        _items.CopyTo(items, 0);
        other._items.CopyTo(items, _items.Length);
        return new Seq<T>(items);
    }

    public Seq<T> Insert(int index, T item) {
        var items = new T[_items.Length + 1];
        Array.Copy(_items, 0, items, 0, index);
        items[index] = item;
        Array.Copy(_items, index, items, index + 1, _items.Length - index);
        return new Seq<T>(items);
    }

    public Seq<T> RemoveAt(int index) {
        if (_items.Length == 1) {
            return Empty;
        }

        var items = new T[_items.Length - 1];
        Array.Copy(_items, 0, items, 0, index);
        Array.Copy(_items, index + 1, items, index, _items.Length - index - 1);
        return new Seq<T>(items);
    }

    /// <summary>
    /// The sequence without the first item that equals the given one.
    /// </summary>
    public Seq<T> Remove(T item) {
        var index = Array.IndexOf(_items, item);
        return index < 0 ? this : RemoveAt(index);
    }

    /// <summary>
    /// The sequence without the items from an index on.
    /// </summary>
    public Seq<T> Take(int count) {
        if (count >= _items.Length) {
            return this;
        }

        return count == 0 ? Empty : new Seq<T>(_items[..count]);
    }

    public bool Contains(T item) => Array.IndexOf(_items, item) >= 0;

    public void Walk(Hasher hasher) {
        hasher.Add((ulong) _items.Length);
        var add = Walker.Adder<T>.Add;
        foreach (var item in _items) {
            add(item, hasher);
        }
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>) _items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public override string ToString() => "[" + string.Join(", ", _items) + "]";
}

/// <summary>
/// A 128-bit hash of a state, built from two lanes that mix every word differently.
/// </summary>
public sealed class Hasher {
    private ulong _a = 0x243F6A8885A308D3UL;
    private ulong _b = 0x13198A2E03707344UL;
    private ulong _count;

    public void Reset() {
        _a = 0x243F6A8885A308D3UL;
        _b = 0x13198A2E03707344UL;
        _count = 0;
    }

    public void Add(ulong value) {
        _count++;
        _a = BitOperations.RotateLeft(_a ^ Mix(value), 27) * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL;
        _b = BitOperations.RotateLeft(_b + Mix(value ^ 0x5BD1E9955BD1E995UL), 31) * 0xC2B2AE3D27D4EB4FUL;
    }

    public void Add(string? value) {
        if (value == null) {
            Add(0x6E756C6CUL);
            return;
        }

        Add((ulong) value.Length);
        foreach (var c in value) {
            Add(c);
        }
    }

    public void Add(double value) => Add((ulong) BitConverter.DoubleToInt64Bits(value));

    public UInt128 Result => new(Mix(_a ^ _count), Mix(_b + _count * 0x9E3779B97F4A7C15UL));

    private static ulong Mix(ulong k) {
        k ^= k >> 33;
        k *= 0xFF51AFD7ED558CCDUL;
        k ^= k >> 33;
        k *= 0xC4CEB9FE1A85EC53UL;
        k ^= k >> 33;
        return k;
    }
}

/// <summary>
/// Adds values to a hash field by field, with a walker compiled once for each type.
/// </summary>
public static class Walker {
    private static readonly ConcurrentDictionary<Type, Action<object, Hasher>> Walkers = new();

    private static readonly MethodInfo AddULong = typeof(Hasher).GetMethod(nameof(Hasher.Add), [typeof(ulong)])!;
    private static readonly MethodInfo AddDouble = typeof(Hasher).GetMethod(nameof(Hasher.Add), [typeof(double)])!;
    private static readonly MethodInfo AddString = typeof(Hasher).GetMethod(nameof(Hasher.Add), [typeof(string)])!;
    private static readonly MethodInfo WalkObject = typeof(Walker).GetMethod(nameof(Walk))!;

    public static UInt128 Hash(object value) {
        var hasher = new Hasher();
        Walk(value, hasher);
        return hasher.Result;
    }

    public static void Walk(object? value, Hasher hasher) {
        switch (value) {
            case null:
                hasher.Add(0x6E756C6CUL);
                return;
            case string text:
                hasher.Add(text);
                return;
            case IWalkable walkable:
                walkable.Walk(hasher);
                return;
        }

        var type = value.GetType();
        if (!Walkers.TryGetValue(type, out var walker)) {
            walker = Walkers.GetOrAdd(type, Build(type));
        }

        walker(value, hasher);
    }

    /// <summary>
    /// Adds a value of a type known up front, without boxing it.
    /// </summary>
    public static class Adder<T> {
        public static readonly Action<T, Hasher> Add = Build();

        private static Action<T, Hasher> Build() {
            var value = Expression.Parameter(typeof(T), "value");
            var hasher = Expression.Parameter(typeof(Hasher), "hasher");
            return Expression.Lambda<Action<T, Hasher>>(AddValue(value, hasher), value, hasher).Compile();
        }
    }

    private static Action<object, Hasher> Build(Type type) {
        var value = Expression.Parameter(typeof(object), "value");
        var hasher = Expression.Parameter(typeof(Hasher), "hasher");
        var typed = Expression.Variable(type, "typed");
        var body = new List<Expression> {
            Expression.Assign(typed, Expression.Convert(value, type)),
            Expression.Call(hasher, AddULong, Expression.Constant(TypeId(type)))
        };
        body.AddRange(Fields.Of(type).Select(field => AddValue(Expression.Field(typed, field), hasher)));
        body.Add(Expression.Empty());
        return Expression.Lambda<Action<object, Hasher>>(Expression.Block([typed], body), value, hasher).Compile();
    }

    private static Expression AddValue(Expression value, ParameterExpression hasher) {
        var type = value.Type;
        if (type == typeof(bool)) {
            return Expression.Call(
                hasher, AddULong, Expression.Condition(value, Expression.Constant(1UL), Expression.Constant(2UL))
            );
        }

        if (type.IsEnum) {
            return AddValue(Expression.Convert(value, Enum.GetUnderlyingType(type)), hasher);
        }

        if (type == typeof(sbyte) || type == typeof(short) || type == typeof(int) || type == typeof(long)) {
            return Expression.Call(
                hasher, AddULong, Expression.Convert(Expression.Convert(value, typeof(long)), typeof(ulong))
            );
        }

        if (type == typeof(byte) || type == typeof(ushort) || type == typeof(uint) || type == typeof(ulong) ||
            type == typeof(char)) {
            return Expression.Call(hasher, AddULong, Expression.Convert(value, typeof(ulong)));
        }

        if (type == typeof(float) || type == typeof(double)) {
            return Expression.Call(hasher, AddDouble, Expression.Convert(value, typeof(double)));
        }

        if (type == typeof(string)) {
            return Expression.Call(hasher, AddString, value);
        }

        if (Nullable.GetUnderlyingType(type) is { } inner) {
            return Expression.IfThenElse(
                Expression.Property(value, "HasValue"),
                Expression.Block(
                    Expression.Call(hasher, AddULong, Expression.Constant(1UL)),
                    AddValue(Expression.Call(value, type.GetMethod("GetValueOrDefault", Type.EmptyTypes)!), hasher)
                ),
                Expression.Call(hasher, AddULong, Expression.Constant(0UL))
            );
        }

        if (type.IsValueType) {
            var fields = Fields.Of(type).Select(field => AddValue(Expression.Field(value, field), hasher)).ToList();
            fields.Add(Expression.Empty());
            return Expression.Block(fields);
        }

        return Expression.Call(WalkObject, Expression.Convert(value, typeof(object)), hasher);
    }

    private static ulong TypeId(Type type) {
        var id = 0xCBF29CE484222325UL;
        foreach (var c in type.FullName ?? type.Name) {
            id = (id ^ c) * 0x100000001B3UL;
        }

        return id;
    }
}

/// <summary>
/// Copies records, with a list of the fields that hold records worked out once for each type.
/// </summary>
internal static class Cloner {
    private static readonly ConcurrentDictionary<Type, Func<Rec, Rec>> Cloners = new();

    private static readonly MethodInfo ShallowCopy =
        typeof(Rec).GetMethod(nameof(Rec.ShallowCopy), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo CloneMethod = typeof(Cloner).GetMethod(nameof(Clone))!;

    public static Rec Clone(Rec source) {
        var type = source.GetType();
        if (!Cloners.TryGetValue(type, out var cloner)) {
            cloner = Cloners.GetOrAdd(type, Build(type));
        }

        return cloner(source);
    }

    /// <summary>
    /// A copy of the object, with a copy of each record in its fields in place of the record.
    /// </summary>
    private static Func<Rec, Rec> Build(Type type) {
        var source = Expression.Parameter(typeof(Rec), "source");
        var typed = Expression.Variable(type, "typed");
        var copy = Expression.Variable(type, "copy");
        var body = new List<Expression> {
            Expression.Assign(typed, Expression.Convert(source, type)),
            Expression.Assign(copy, Expression.Convert(Expression.Call(source, ShallowCopy), type))
        };
        foreach (var field in Fields.Of(type).Where(field => typeof(Rec).IsAssignableFrom(field.FieldType))) {
            var value = Expression.Field(typed, field);
            body.Add(Expression.IfThen(
                Expression.NotEqual(value, Expression.Constant(null, field.FieldType)),
                Expression.Assign(
                    Expression.Field(copy, field),
                    Expression.Convert(Expression.Call(CloneMethod, value), field.FieldType)
                )
            ));
        }

        body.Add(Expression.Convert(copy, typeof(Rec)));
        return Expression.Lambda<Func<Rec, Rec>>(Expression.Block([typed, copy], body), source).Compile();
    }
}

/// <summary>
/// The fields of types, for hashing, copying and showing states.
/// </summary>
public static class Fields {
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> Cache = new();

    /// <summary>
    /// Every instance field of a type and its base types, base types first, in the order they are declared.
    /// </summary>
    public static FieldInfo[] Of(Type type) => Cache.GetOrAdd(type, found => {
        var chain = new List<Type>();
        for (var current = found; current != null && current != typeof(object); current = current.BaseType) {
            chain.Insert(0, current);
        }

        return chain.SelectMany(current => current.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly
            ))
            .OrderBy(field => field.MetadataToken)
            .ToArray();
    });

    /// <summary>
    /// The name a field is shown under: the property's name for the field behind a property.
    /// </summary>
    public static string NameOf(FieldInfo field) {
        var name = field.Name;
        return name.StartsWith('<') && name.IndexOf('>') is var end and > 0 ? name[1..end] : name;
    }

    /// <summary>
    /// The fields of a record and the records inside it as one flat list, for showing what an event changed.
    /// </summary>
    public static IEnumerable<(string Name, string Value)> Flatten(Rec record, string prefix = "") {
        foreach (var field in Of(record.GetType())) {
            var value = field.GetValue(record);
            var name = prefix + NameOf(field);
            if (value is Rec inner) {
                foreach (var pair in Flatten(inner, name + ".")) {
                    yield return pair;
                }
            } else {
                yield return (name, Format(value));
            }
        }
    }

    public static string Format(object? value) => value switch {
        null => "null",
        bool flag => flag ? "true" : "false",
        string text => $"\"{text}\"",
        _ => value.ToString() ?? ""
    };

    /// <summary>
    /// A text with the names and values of fields, for describing states.
    /// </summary>
    public static string Describe(Rec record) {
        var builder = new StringBuilder();
        foreach (var (name, value) in Flatten(record)) {
            if (builder.Length > 0) {
                builder.Append(", ");
            }

            builder.Append(name).Append('=').Append(value);
        }

        return builder.ToString();
    }
}
