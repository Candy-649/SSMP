using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client;

/// <summary>
/// The picture of a round of a game that each player plays on their own, which the game of the player who plays it
/// sends to the games of the players who watch it (see <see cref="MinigameCoop"/>). Everything of such a round runs in
/// the game of the player who plays it alone, so a player who watches would otherwise see them jump at nothing.
///
/// What is sent is what the round's place in the room draws (see <see cref="PersonalPlaces"/>): every part of it that
/// draws a sprite, by the number it has in every game, with where it is, how it is turned and sized, which sprite it
/// draws and in which colour; and the things that the place throws out from the pools of the game while the round goes
/// on, like what is thrown at the player, by their name. With it goes where the camera of the player who plays is.
/// The watching game draws each of them with a figure of its own, made from its own part with the same number or its
/// own thing with the same name, with nothing on it but what draws it, puts its camera where the player's is, and
/// hides its own parts of the place for as long as it watches (see <see cref="ViewedParts"/>).
///
/// It is sent many times a second the way the animations of a player go, which only reach the players in the same room
/// and may be lost on the way: only what changed since the last time is sent, and the whole picture once a second, so
/// a lost piece is put right soon. Nothing is sent while nobody watches.
/// </summary>
internal sealed class MinigameMirror {
    /// <summary>
    /// How often the picture is sent, in seconds.
    /// </summary>
    private const float SendEvery = 1f / 15f;

    /// <summary>
    /// How often the whole picture is sent rather than what changed, in seconds.
    /// </summary>
    private const float WholeEvery = 1f;

    /// <summary>
    /// The longest piece that the way of animations takes, in bytes.
    /// </summary>
    private const int PieceLength = 255;

    /// <summary>
    /// The first byte of every piece, which changes whenever the way they are written does.
    /// </summary>
    private const byte PieceVersion = 1;

    /// <summary>
    /// A piece with the names of the sheets that sprites are drawn from.
    /// </summary>
    private const byte NamesPiece = 1;

    /// <summary>
    /// A piece with what parts draw.
    /// </summary>
    private const byte PartsPiece = 2;

    /// <summary>
    /// The flag of a parts piece that is part of the whole picture.
    /// </summary>
    private const byte WholeFlag = 1;

    /// <summary>
    /// The length of the head of a parts piece: its version, kind, flags, number (2), number of the whole picture,
    /// number of the piece in it, how many pieces it has, the place, how many parts the place has (2) and where the
    /// camera is (8).
    /// </summary>
    private const int PartsHead = 19;

    /// <summary>
    /// The bit of the number of a part that says it is a thing that the place threw out.
    /// </summary>
    private const int ThrownBit = 0x8000;

    /// <summary>
    /// How many of the things that the place threw out are kept track of at once.
    /// </summary>
    private const int MostThrown = 64;

    /// <summary>
    /// How long a thing that the place threw out stays tracked after it was put away, in seconds.
    /// </summary>
    private const float ThrownGoneAfter = 1f;

    /// <summary>
    /// The fields of a part in a piece: that it shows at all, where it is, how it is turned, how large, which sprite it
    /// draws, in which colour, and for a thing that the place threw out, its name.
    /// </summary>
    private const byte ShownField = 1, PlaceField = 2, TurnField = 4, SizeField = 8, SpriteField = 16,
        ColorField = 32, NameField = 64;

    /// <summary>
    /// The sprite of a part that draws with a sprite of Unity's own, which is found by its name.
    /// </summary>
    private const ushort NamedSprite = ushort.MaxValue;

    /// <summary>
    /// The number of no sheet at all.
    /// </summary>
    private const byte NoSheet = byte.MaxValue;

    /// <summary>
    /// How far a part has to have moved to be sent again, in units.
    /// </summary>
    private const float MoveEnough = 0.004f;

    /// <summary>
    /// How fast a figure follows where its part is, as a rate per second.
    /// </summary>
    private const float FollowRate = 20f;

    /// <summary>
    /// How far off a figure may be before it is put where it is rather than moved there, in units.
    /// </summary>
    private const float JumpAfter = 4f;

    /// <summary>
    /// The longest name that is sent, in bytes.
    /// </summary>
    private const int LongestName = 60;

    #region Sending

    /// <summary>
    /// The handle of the room whose place is sent, or -1 while nothing is.
    /// </summary>
    private int _sendScene = -1;

    /// <summary>
    /// The number of the place that is sent.
    /// </summary>
    private int _sendPlace = -1;

    /// <summary>
    /// How many parts the place that is sent has, which the watching game checks against its own.
    /// </summary>
    private int _sendPartCount;

    /// <summary>
    /// The parts of the place that is sent that draw, each with the number it has in every game.
    /// </summary>
    private readonly List<Drawn> _sendParts = [];

    /// <summary>
    /// The things that the place threw out while it was sent.
    /// </summary>
    private readonly List<Drawn> _thrown = [];

    /// <summary>
    /// The number that the next thing thrown out gets.
    /// </summary>
    private int _nextThrownNumber;

    /// <summary>
    /// The sheets that sprites are drawn from, by their name, each with the number it goes under.
    /// </summary>
    private readonly Dictionary<string, byte> _sheetNumbers = new(StringComparer.Ordinal);

    /// <summary>
    /// The names of the sheets, by their number.
    /// </summary>
    private readonly List<string> _sheetNames = [];

    /// <summary>
    /// The number of each sheet of the game's own, or sprite of Unity's, that a part drew, by the object itself:
    /// reading the name of one makes a new string every time, which every part sent many times a second would pile up.
    /// </summary>
    private readonly Dictionary<Object, byte> _sheetNumberOf = new();

    /// <summary>
    /// Where each piece of the picture being sent starts and ends among the entries, kept to be filled again.
    /// </summary>
    private readonly List<(int From, int To)> _pieces = [];

    /// <summary>
    /// Whether a sheet got a number since the names were last sent.
    /// </summary>
    private bool _namesChanged;

    /// <summary>
    /// When the picture is sent next, in unscaled time.
    /// </summary>
    private float _nextSendAt;

    /// <summary>
    /// When the whole picture is sent next, in unscaled time.
    /// </summary>
    private float _nextWholeAt;

    /// <summary>
    /// The number of the last picture that was sent.
    /// </summary>
    private ushort _tick;

    /// <summary>
    /// The number of the last whole picture that was sent.
    /// </summary>
    private byte _wholeNumber;

    /// <summary>
    /// What the parts of the picture being sent say, one after another.
    /// </summary>
    private readonly byte[] _entries = new byte[32768];

    /// <summary>
    /// How much of <see cref="_entries"/> is filled.
    /// </summary>
    private int _entriesLength;

    /// <summary>
    /// Where each part starts in <see cref="_entries"/>.
    /// </summary>
    private readonly List<int> _entryStarts = [];

    /// <summary>
    /// Whether sending something went wrong, which is only logged once.
    /// </summary>
    private bool _sendFailed;

    /// <summary>
    /// A part, or a thing thrown out, that may draw, with what it draws with and what was last sent of it.
    /// </summary>
    private sealed class Drawn {
        public GameObject? Object;
        public Renderer? Renderer;
        public tk2dBaseSprite? Tk;
        public SpriteRenderer? Sprite;
        public int Number;
        public string? Name;
        public bool Said;
        public Look Sent;
        public float PutAwayAt = -1f;
    }

    /// <summary>
    /// What a part draws.
    /// </summary>
    private struct Look {
        public bool Shown;
        public Vector3 Place;
        public ushort Turn;
        public ushort SizeX;
        public ushort SizeY;
        public byte Sheet;
        public ushort Sprite;
        public Color32 Color;
    }

    /// <summary>
    /// Whether the picture of a place is being sent.
    /// </summary>
    public bool IsSending => _sendScene >= 0;

    /// <summary>
    /// Stops sending, and forgets what was sent.
    /// </summary>
    public void StopSending() {
        _sendScene = -1;
        _sendPlace = -1;
        _sendParts.Clear();
        _thrown.Clear();
        _sheetNumbers.Clear();
        _sheetNames.Clear();
        _sheetNumberOf.Clear();
    }

    /// <summary>
    /// Sends the whole picture with the next piece rather than within a second, for a player who just began to watch.
    /// </summary>
    public void SendWholeSoon() {
        _nextSendAt = 0f;
        _nextWholeAt = 0f;
    }

    /// <summary>
    /// Takes a thing that the place threw out from the pools of the game, to be sent with the place while it shows.
    /// </summary>
    /// <param name="thing">The thing.</param>
    public void AddThrown(GameObject thing) {
        if (!IsSending || thing == null) {
            return;
        }

        foreach (var known in _thrown) {
            if (known.Object == thing) {
                // The same thing thrown again is a new thing for the watching game, which may still show the last one
                known.Number = TakeThrownNumber();
                known.Said = false;
                known.PutAwayAt = -1f;
                return;
            }
        }

        if (_thrown.Count >= MostThrown) {
            return;
        }

        var drawn = Describe(thing);
        if (drawn.Renderer == null) {
            return;
        }

        drawn.Number = TakeThrownNumber();
        drawn.Name = NameOf(thing);
        _thrown.Add(drawn);
    }

    /// <summary>
    /// Sends what the place shows now, at most every <see cref="SendEvery"/> seconds.
    /// </summary>
    /// <param name="scene">The room of the place.</param>
    /// <param name="place">The number of the place (see <see cref="PersonalPlaces.TryFindPlace"/>).</param>
    /// <param name="camera">Where the camera of the local player is.</param>
    /// <param name="send">Sends a piece of the picture.</param>
    public void Send(Scene scene, int place, Vector2 camera, Action<byte[]> send) {
        if (scene.handle != _sendScene || place != _sendPlace) {
            StopSending();
            var parts = PersonalPlaces.GetParts(scene, place);
            if (parts == null) {
                return;
            }

            _sendScene = scene.handle;
            _sendPlace = place;
            _sendPartCount = parts.Count;
            for (var number = 0; number < parts.Count; number++) {
                if (parts[number] is { } part && part != null) {
                    var drawn = Describe(part);
                    if (drawn.Renderer != null) {
                        drawn.Number = number;
                        _sendParts.Add(drawn);
                    }
                }
            }

            _nextSendAt = 0f;
            _nextWholeAt = 0f;
        }

        var now = Time.unscaledTime;
        if (now < _nextSendAt) {
            return;
        }

        _nextSendAt = now + SendEvery;
        var whole = now >= _nextWholeAt;
        if (whole) {
            _nextWholeAt = now + WholeEvery;
            _wholeNumber++;
        }

        _tick++;
        _entriesLength = 0;
        _entryStarts.Clear();

        try {
            foreach (var drawn in _sendParts) {
                WriteIfChanged(drawn, whole);
            }

            for (var index = _thrown.Count - 1; index >= 0; index--) {
                var drawn = _thrown[index];
                WriteIfChanged(drawn, whole);

                // A thing that was put away a while ago is forgotten, once the watching game was told so
                if (drawn.Object == null ||
                    !drawn.Sent.Shown && drawn.PutAwayAt >= 0f && now - drawn.PutAwayAt > ThrownGoneAfter) {
                    _thrown.RemoveAt(index);
                }
            }

            if (whole || _namesChanged) {
                _namesChanged = false;
                SendNames(send);
            }

            SendParts(whole, camera, send);
        } catch (Exception e) {
            if (!_sendFailed) {
                _sendFailed = true;
                Logger.Warn($"Could not send the picture of the round: {e.GetType()}, {e.Message}");
            }
        }
    }

    /// <summary>
    /// Takes the next number for a thing thrown out.
    /// </summary>
    private int TakeThrownNumber() {
        var number = ThrownBit | (_nextThrownNumber & (ThrownBit - 1));
        _nextThrownNumber++;
        return number;
    }

    /// <summary>
    /// Finds what a part draws with: a sprite of the game's own sheets, or a sprite of Unity's. A part that draws
    /// neither is not sent, nor is one whose drawing is only particles or text.
    /// </summary>
    private static Drawn Describe(GameObject part) {
        var drawn = new Drawn { Object = part };
        if (part.TryGetComponent<tk2dBaseSprite>(out var tk) && part.TryGetComponent<MeshRenderer>(out var mesh)) {
            drawn.Tk = tk;
            drawn.Renderer = mesh;
        } else if (part.TryGetComponent<SpriteRenderer>(out var sprite)) {
            drawn.Sprite = sprite;
            drawn.Renderer = sprite;
        }

        return drawn;
    }

    /// <summary>
    /// The name that a thing thrown out is found by in every game: its own, without what copying it added.
    /// </summary>
    private static string NameOf(GameObject thing) {
        var name = thing.name;
        var copied = name.IndexOf("(Clone)", StringComparison.Ordinal);
        return (copied >= 0 ? name.Substring(0, copied) : name).Trim();
    }

    /// <summary>
    /// Reads what a part draws now.
    /// </summary>
    private Look ReadLook(Drawn drawn) {
        var look = new Look();
        var part = drawn.Object;
        var renderer = drawn.Renderer;
        if (part == null || renderer == null || !part.activeInHierarchy || !renderer.enabled ||
            renderer.forceRenderingOff) {
            return look;
        }

        look.Shown = true;
        var transform = part.transform;
        look.Place = transform.position;
        look.Turn = (ushort) ((int) (Mathf.Repeat(transform.eulerAngles.z, 360f) / 360f * 65536f) & 0xFFFF);

        var size = transform.lossyScale;
        Object? sheet;
        if (drawn.Tk is { } tk && tk != null) {
            var own = tk.scale;
            size.x *= own.x;
            size.y *= own.y;
            sheet = tk.Collection;
            look.Sprite = (ushort) Mathf.Clamp(tk.spriteId, 0, NamedSprite - 1);
            look.Color = tk.color;
        } else if (drawn.Sprite is { } sprite && sprite != null) {
            if (sprite.flipX) {
                size.x = -size.x;
            }

            if (sprite.flipY) {
                size.y = -size.y;
            }

            sheet = sprite.sprite;
            look.Sprite = NamedSprite;
            look.Color = sprite.color;
        } else {
            look.Shown = false;
            return look;
        }

        look.SizeX = Mathf.FloatToHalf(size.x);
        look.SizeY = Mathf.FloatToHalf(size.y);
        look.Sheet = NumberOfSheet(sheet);
        return look;
    }

    /// <summary>
    /// The number that a sheet of the game's own, or a sprite of Unity's, goes under, which is the number of its name.
    /// </summary>
    private byte NumberOfSheet(Object? sheet) {
        if (sheet == null) {
            return NoSheet;
        }

        if (!_sheetNumberOf.TryGetValue(sheet, out var number)) {
            number = NumberOfSheet(sheet.name);
            _sheetNumberOf[sheet] = number;
        }

        return number;
    }

    /// <summary>
    /// The number that the name of a sheet goes under, given to it the first time it is asked for.
    /// </summary>
    private byte NumberOfSheet(string name) {
        if (_sheetNumbers.TryGetValue(name, out var number)) {
            return number;
        }

        if (_sheetNames.Count >= NoSheet) {
            return NoSheet;
        }

        number = (byte) _sheetNames.Count;
        _sheetNumbers[name] = number;
        _sheetNames.Add(name);
        _namesChanged = true;
        return number;
    }

    /// <summary>
    /// Writes what a part draws if it changed since it was last sent, or if the whole picture is sent and it shows. A
    /// part that stopped showing is written once, as not shown; the whole picture leaves out the parts that do not
    /// show, which the watching game takes as not shown.
    /// </summary>
    private void WriteIfChanged(Drawn drawn, bool whole) {
        var look = ReadLook(drawn);
        if (!look.Shown) {
            if (drawn.Said && drawn.Sent.Shown) {
                StartEntry(drawn.Number, 0);
            }

            if (drawn.PutAwayAt < 0f || drawn.Sent.Shown) {
                drawn.PutAwayAt = Time.unscaledTime;
            }

            drawn.Sent = look;
            drawn.Said = true;
            return;
        }

        drawn.PutAwayAt = -1f;
        var fresh = whole || !drawn.Said || !drawn.Sent.Shown;
        var sent = drawn.Sent;
        var fields = ShownField;
        if (fresh || (look.Place - sent.Place).sqrMagnitude > MoveEnough * MoveEnough) {
            fields |= PlaceField;
        }

        if (fresh || look.Turn != sent.Turn) {
            fields |= TurnField;
        }

        if (fresh || look.SizeX != sent.SizeX || look.SizeY != sent.SizeY) {
            fields |= SizeField;
        }

        if (fresh || look.Sheet != sent.Sheet || look.Sprite != sent.Sprite) {
            fields |= SpriteField;
        }

        if (fresh || look.Color.r != sent.Color.r || look.Color.g != sent.Color.g || look.Color.b != sent.Color.b ||
            look.Color.a != sent.Color.a) {
            fields |= ColorField;
        }

        if (fresh && drawn.Name != null) {
            fields |= NameField;
        }

        if (fields == ShownField) {
            return;
        }

        StartEntry(drawn.Number, (byte) fields);
        if ((fields & PlaceField) != 0) {
            WriteFloat(look.Place.x);
            WriteFloat(look.Place.y);
            WriteFloat(look.Place.z);
        } else {
            // A part that creeps along is sent once it has gone far enough from where it was last sent
            look.Place = sent.Place;
        }

        if ((fields & TurnField) != 0) {
            WriteUShort(look.Turn);
        }

        if ((fields & SizeField) != 0) {
            WriteUShort(look.SizeX);
            WriteUShort(look.SizeY);
        }

        if ((fields & SpriteField) != 0) {
            _entries[_entriesLength++] = look.Sheet;
            WriteUShort(look.Sprite);
        }

        if ((fields & ColorField) != 0) {
            _entries[_entriesLength++] = look.Color.r;
            _entries[_entriesLength++] = look.Color.g;
            _entries[_entriesLength++] = look.Color.b;
            _entries[_entriesLength++] = look.Color.a;
        }

        if ((fields & NameField) != 0) {
            WriteName(drawn.Name!);
        }

        drawn.Sent = look;
        drawn.Said = true;
    }

    /// <summary>
    /// Starts the entry of a part, unless the picture has no room left for it.
    /// </summary>
    private void StartEntry(int number, byte fields) {
        if (_entriesLength > _entries.Length - PieceLength) {
            throw new InvalidOperationException("the picture is too large");
        }

        _entryStarts.Add(_entriesLength);
        WriteUShort((ushort) number);
        _entries[_entriesLength++] = fields;
    }

    private void WriteUShort(ushort value) {
        _entries[_entriesLength++] = (byte) value;
        _entries[_entriesLength++] = (byte) (value >> 8);
    }

    private void WriteFloat(float value) {
        var bits = BitConverter.SingleToInt32Bits(value);
        _entries[_entriesLength++] = (byte) bits;
        _entries[_entriesLength++] = (byte) (bits >> 8);
        _entries[_entriesLength++] = (byte) (bits >> 16);
        _entries[_entriesLength++] = (byte) (bits >> 24);
    }

    private void WriteName(string name) {
        var bytes = Encoding.UTF8.GetBytes(name);
        var length = System.Math.Min(bytes.Length, LongestName);
        _entries[_entriesLength++] = (byte) length;
        Array.Copy(bytes, 0, _entries, _entriesLength, length);
        _entriesLength += length;
    }

    /// <summary>
    /// Sends the names of the sheets, in as many pieces as they take.
    /// </summary>
    private void SendNames(Action<byte[]> send) {
        var piece = new List<byte>(PieceLength) { PieceVersion, NamesPiece, 0 };
        var count = 0;
        for (var number = 0; number < _sheetNames.Count; number++) {
            var bytes = Encoding.UTF8.GetBytes(_sheetNames[number]);
            var length = System.Math.Min(bytes.Length, LongestName);
            if (piece.Count + 2 + length > PieceLength) {
                piece[2] = (byte) count;
                send(piece.ToArray());
                piece.RemoveRange(3, piece.Count - 3);
                count = 0;
            }

            piece.Add((byte) number);
            piece.Add((byte) length);
            for (var index = 0; index < length; index++) {
                piece.Add(bytes[index]);
            }

            count++;
        }

        if (count > 0) {
            piece[2] = (byte) count;
            send(piece.ToArray());
        }
    }

    /// <summary>
    /// Sends the entries that were written, in as many pieces as they take. The pieces of the whole picture say how
    /// many there are, so that the watching game can tell when it has them all.
    /// </summary>
    private void SendParts(bool whole, Vector2 camera, Action<byte[]> send) {
        // Even with nothing changed, the camera of a player who plays may have moved
        var pieces = _pieces;
        pieces.Clear();
        var start = 0;
        while (start < _entryStarts.Count || pieces.Count == 0) {
            var end = start;
            var length = PartsHead;
            while (end < _entryStarts.Count) {
                var entryLength = EntryEnd(end) - _entryStarts[end];
                if (length + entryLength > PieceLength) {
                    break;
                }

                length += entryLength;
                end++;
            }

            pieces.Add((start, end));
            if (end == start) {
                break;
            }

            start = end;
        }

        for (var index = 0; index < pieces.Count; index++) {
            var (from, to) = pieces[index];
            var bodyStart = from < _entryStarts.Count ? _entryStarts[from] : _entriesLength;
            var bodyEnd = to < _entryStarts.Count ? _entryStarts[to] : _entriesLength;
            var piece = new byte[PartsHead + bodyEnd - bodyStart];
            piece[0] = PieceVersion;
            piece[1] = PartsPiece;
            piece[2] = whole ? WholeFlag : (byte) 0;
            piece[3] = (byte) _tick;
            piece[4] = (byte) (_tick >> 8);
            piece[5] = _wholeNumber;
            piece[6] = (byte) index;
            piece[7] = (byte) pieces.Count;
            piece[8] = (byte) _sendPlace;
            piece[9] = (byte) _sendPartCount;
            piece[10] = (byte) (_sendPartCount >> 8);
            WriteFloatInto(piece, 11, camera.x);
            WriteFloatInto(piece, 15, camera.y);
            Array.Copy(_entries, bodyStart, piece, PartsHead, bodyEnd - bodyStart);
            send(piece);
        }
    }

    /// <summary>
    /// Where the entry with an index ends in <see cref="_entries"/>.
    /// </summary>
    private int EntryEnd(int index) => index + 1 < _entryStarts.Count ? _entryStarts[index + 1] : _entriesLength;

    private static void WriteFloatInto(byte[] piece, int at, float value) {
        var bits = BitConverter.SingleToInt32Bits(value);
        piece[at] = (byte) bits;
        piece[at + 1] = (byte) (bits >> 8);
        piece[at + 2] = (byte) (bits >> 16);
        piece[at + 3] = (byte) (bits >> 24);
    }

    #endregion

    #region Viewing

    /// <summary>
    /// The character of the player whose round is watched, whose pieces are taken, or null while none is.
    /// </summary>
    private GameObject? _viewOf;

    /// <summary>
    /// The handle of the room whose place is watched.
    /// </summary>
    private int _viewScene = -1;

    /// <summary>
    /// The number of the place that is watched.
    /// </summary>
    private int _viewPlace = -1;

    /// <summary>
    /// The local game's own parts of the place that is watched, which the figures are made from.
    /// </summary>
    private IReadOnlyList<GameObject>? _ownParts;

    /// <summary>
    /// What the figures are kept under, in the room.
    /// </summary>
    private GameObject? _viewRoot;

    /// <summary>
    /// The figures, by the number of the part or thing they show.
    /// </summary>
    private readonly Dictionary<int, Figure> _figures = new();

    /// <summary>
    /// The names of the sheets, by the number they go under in the pieces that are taken.
    /// </summary>
    private readonly Dictionary<byte, string> _viewSheetNames = new();

    /// <summary>
    /// The sheets of sprites of the game, by their name, found once each. A name that none has is kept as null.
    /// </summary>
    private readonly Dictionary<string, tk2dSpriteCollectionData?> _sheets = new(StringComparer.Ordinal);

    /// <summary>
    /// The sprites of Unity's own, by their name, found once each. A name that none has is kept as null.
    /// </summary>
    private readonly Dictionary<string, Sprite?> _unitySprites = new(StringComparer.Ordinal);

    /// <summary>
    /// The things of the game that a thing thrown out is drawn like, by their name, found once each.
    /// </summary>
    private readonly Dictionary<string, GameObject?> _thrownLooks = new(StringComparer.Ordinal);

    /// <summary>
    /// The handle of the room that the sheets, sprites and things above were found in. They are kept for as long as
    /// rounds are watched in that room, since finding them looks through everything the game has loaded.
    /// </summary>
    private int _foundInScene = -1;


    /// <summary>
    /// The number of the newest picture taken, and whether one was.
    /// </summary>
    private ushort _newestTick;

    private bool _anyTick;

    /// <summary>
    /// The number of the whole picture being taken, how many of its pieces came, and which parts they listed.
    /// </summary>
    private int _wholeTaking = -1;

    private int _wholePiecesTaken;

    private readonly HashSet<int> _wholeListed = [];

    /// <summary>
    /// Where the camera of the player who plays was last said to be.
    /// </summary>
    private Vector2? _cameraTarget;

    /// <summary>
    /// Whether taking a piece went wrong, which is only logged once.
    /// </summary>
    private bool _takeFailed;

    /// <summary>
    /// A figure that shows a part of the place, or a thing that it threw out, of the player who plays.
    /// </summary>
    private sealed class Figure {
        public required GameObject Object;
        public required Transform Transform;
        public tk2dBaseSprite? Tk;
        public SpriteRenderer? Sprite;
        public Vector3 Target;
        public float TargetTurn;
        public bool Placed;

        /// <summary>
        /// For a thing thrown out, its name, by which its figure can show another such thing once it is put away.
        /// </summary>
        public string? Name;

        /// <summary>
        /// When it was last hidden, in unscaled time.
        /// </summary>
        public float HiddenAt;

        /// <summary>
        /// Hides it, noting when.
        /// </summary>
        public void Hide() {
            if (Object != null && Object.activeSelf) {
                Object.SetActive(false);
                HiddenAt = Time.unscaledTime;
            }
        }
    }

    /// <summary>
    /// Where to put the camera to see what the player who plays sees, or null until it is known.
    /// </summary>
    public Vector2? Camera { get; private set; }

    /// <summary>
    /// The local game's own parts of the place that is watched, which are hidden while it is, or null while nothing is
    /// watched.
    /// </summary>
    public IReadOnlyList<GameObject>? ViewedParts => _viewOf != null ? _ownParts : null;

    /// <summary>
    /// Whether a round in a room is watched.
    /// </summary>
    public bool IsViewingIn(Scene scene) => _viewOf != null && scene.handle == _viewScene;

    /// <summary>
    /// Whether the place that is watched has a different number of parts in the game of the player who plays it than
    /// here, which would put every figure on the wrong part: nothing of it is shown.
    /// </summary>
    public bool IsMismatched { get; private set; }

    /// <summary>
    /// Starts taking the picture of a round from the game of a player.
    /// </summary>
    /// <param name="playerObject">The character of the player who plays.</param>
    /// <param name="scene">The room.</param>
    /// <param name="place">The number of the place of the round in the room.</param>
    /// <returns>Whether this game has the place.</returns>
    public bool StartViewing(GameObject playerObject, Scene scene, int place) {
        StopViewing();
        var parts = PersonalPlaces.GetParts(scene, place);
        if (parts == null) {
            return false;
        }

        // What was found in another room may be gone with it
        if (_foundInScene != scene.handle) {
            _foundInScene = scene.handle;
            _sheets.Clear();
            _unitySprites.Clear();
            _thrownLooks.Clear();
        }

        _viewOf = playerObject;
        _viewScene = scene.handle;
        _viewPlace = place;
        _ownParts = parts;
        _viewRoot = new GameObject("SSMP Watched Round");
        SceneManager.MoveGameObjectToScene(_viewRoot, scene);
        return true;
    }

    /// <summary>
    /// Stops taking the picture, and takes the figures away.
    /// </summary>
    public void StopViewing() {
        if (_viewRoot != null) {
            Object.Destroy(_viewRoot);
        }

        _viewRoot = null;
        _viewOf = null;
        _viewScene = -1;
        _viewPlace = -1;
        _ownParts = null;
        _figures.Clear();
        _viewSheetNames.Clear();
        _anyTick = false;
        _wholeTaking = -1;
        _wholePiecesTaken = 0;
        _wholeListed.Clear();
        _cameraTarget = null;
        Camera = null;
        IsMismatched = false;
    }

    /// <summary>
    /// Takes a piece of the picture that another player's game sent, if it is from the player whose round is watched.
    /// </summary>
    /// <param name="playerObject">The character of the player who sent it.</param>
    /// <param name="piece">The piece.</param>
    public void Take(GameObject playerObject, byte[] piece) {
        if (_viewOf == null || playerObject != _viewOf || piece.Length < 3 || piece[0] != PieceVersion) {
            return;
        }

        try {
            if (piece[1] == NamesPiece) {
                TakeNames(piece);
            } else if (piece[1] == PartsPiece) {
                TakeParts(piece);
            }
        } catch (Exception e) {
            if (!_takeFailed) {
                _takeFailed = true;
                Logger.Warn($"Could not take a piece of the picture of a round: {e.GetType()}, {e.Message}");
            }
        }
    }

    private void TakeNames(byte[] piece) {
        var at = 3;
        for (var count = 0; count < piece[2] && at + 2 <= piece.Length; count++) {
            var number = piece[at++];
            var length = piece[at++];
            if (at + length > piece.Length) {
                return;
            }

            _viewSheetNames[number] = Encoding.UTF8.GetString(piece, at, length);
            at += length;
        }
    }

    private void TakeParts(byte[] piece) {
        if (piece.Length < PartsHead || piece[8] != _viewPlace || _ownParts == null) {
            return;
        }

        // A place that is not the same in both games would put every figure on the wrong part
        var partCount = piece[9] | piece[10] << 8;
        if (partCount != _ownParts.Count) {
            if (!IsMismatched) {
                IsMismatched = true;
                Logger.Warn($"The watched place has {partCount} parts in the other game and {_ownParts.Count} here");
            }

            return;
        }

        // A piece that was overtaken by a newer one on the way is left out
        var tick = (ushort) (piece[3] | piece[4] << 8);
        if (_anyTick && (short) (tick - _newestTick) < 0) {
            return;
        }

        _anyTick = true;
        _newestTick = tick;
        _cameraTarget = new Vector2(BitConverter.ToSingle(piece, 11), BitConverter.ToSingle(piece, 15));

        var whole = (piece[2] & WholeFlag) != 0;
        if (whole && piece[5] != _wholeTaking) {
            _wholeTaking = piece[5];
            _wholePiecesTaken = 0;
            _wholeListed.Clear();
        }

        var at = PartsHead;
        while (at + 3 <= piece.Length) {
            var number = piece[at] | piece[at + 1] << 8;
            var fields = piece[at + 2];
            at += 3;
            if (whole) {
                _wholeListed.Add(number);
            }

            if (!TakeEntry(number, fields, piece, ref at)) {
                return;
            }
        }

        // Once every piece of the whole picture is in, what it did not list does not show
        if (whole && ++_wholePiecesTaken == piece[7]) {
            foreach (var pair in _figures) {
                if (!_wholeListed.Contains(pair.Key)) {
                    pair.Value.Hide();
                }
            }
        }
    }

    /// <summary>
    /// Takes the entry of a part, and shows it on its figure.
    /// </summary>
    /// <returns>Whether the entry could be read.</returns>
    private bool TakeEntry(int number, byte fields, byte[] piece, ref int at) {
        var length = ((fields & PlaceField) != 0 ? 12 : 0) + ((fields & TurnField) != 0 ? 2 : 0) +
                     ((fields & SizeField) != 0 ? 4 : 0) + ((fields & SpriteField) != 0 ? 3 : 0) +
                     ((fields & ColorField) != 0 ? 4 : 0);
        if (at + length > piece.Length) {
            return false;
        }

        var start = at;
        at += length;
        string? name = null;
        if ((fields & NameField) != 0) {
            if (at >= piece.Length || at + 1 + piece[at] > piece.Length) {
                return false;
            }

            name = Encoding.UTF8.GetString(piece, at + 1, piece[at]);
            at += 1 + piece[at];
        }

        _figures.TryGetValue(number, out var figure);
        if ((fields & ShownField) == 0) {
            figure?.Hide();
            return true;
        }

        if (figure == null || figure.Object == null) {
            // A figure is only made from the first entry that says everything about it
            if ((fields & (PlaceField | SpriteField)) != (PlaceField | SpriteField)) {
                return true;
            }

            // Each throw is a new thing to the game that sends it, even when it is the same thing of the pool thrown
            // again, so the figure of one put away a while ago shows it rather than a figure being made for each throw
            if ((number & ThrownBit) != 0 && name != null && TryFindPutAwayFigure(name, out var was, out var putAway)) {
                _figures.Remove(was);
                figure = putAway;
            } else {
                figure = MakeFigure(number, name);
                if (figure == null) {
                    return true;
                }
            }

            _figures[number] = figure;
        }

        if (!figure.Object.activeSelf) {
            figure.Object.SetActive(true);
            figure.Placed = false;
        }

        var read = start;
        if ((fields & PlaceField) != 0) {
            figure.Target = new Vector3(
                BitConverter.ToSingle(piece, read), BitConverter.ToSingle(piece, read + 4),
                BitConverter.ToSingle(piece, read + 8)
            );
            read += 12;
        }

        if ((fields & TurnField) != 0) {
            figure.TargetTurn = (piece[read] | piece[read + 1] << 8) / 65536f * 360f;
            read += 2;
        }

        if ((fields & SizeField) != 0) {
            figure.Transform.localScale = new Vector3(
                Mathf.HalfToFloat((ushort) (piece[read] | piece[read + 1] << 8)),
                Mathf.HalfToFloat((ushort) (piece[read + 2] | piece[read + 3] << 8)),
                1f
            );
            read += 4;
        }

        if ((fields & SpriteField) != 0) {
            ShowSprite(figure, piece[read], (ushort) (piece[read + 1] | piece[read + 2] << 8));
            read += 3;
        }

        if ((fields & ColorField) != 0) {
            var color = new Color32(piece[read], piece[read + 1], piece[read + 2], piece[read + 3]);
            if (figure.Tk != null) {
                figure.Tk.color = color;
            } else if (figure.Sprite != null) {
                figure.Sprite.color = color;
            }
        }

        if (!figure.Placed) {
            figure.Placed = true;
            figure.Transform.position = figure.Target;
            figure.Transform.rotation = Quaternion.Euler(0f, 0f, figure.TargetTurn);
        }

        return true;
    }

    /// <summary>
    /// Finds the figure of a thing thrown out with a name that was put away long enough ago that the game which sends
    /// the picture has forgotten it (<see cref="ThrownGoneAfter"/>).
    /// </summary>
    /// <param name="name">The name of the thing.</param>
    /// <param name="number">The number that the figure showed.</param>
    /// <param name="figure">The figure.</param>
    /// <returns>Whether one was found.</returns>
    private bool TryFindPutAwayFigure(string name, out int number, out Figure figure) {
        var now = Time.unscaledTime;
        foreach (var pair in _figures) {
            var found = pair.Value;
            if ((pair.Key & ThrownBit) != 0 && found.Object != null && !found.Object.activeSelf &&
                now - found.HiddenAt > ThrownGoneAfter && string.Equals(found.Name, name, StringComparison.Ordinal)) {
                number = pair.Key;
                figure = found;
                return true;
            }
        }

        number = 0;
        figure = null!;
        return false;
    }

    /// <summary>
    /// Shows a sprite on a figure: one of a sheet of the game's own by its number in the sheet, or one of Unity's own
    /// by its name. A sheet or sprite whose name this game has not been told yet is left as it is until it has been.
    /// </summary>
    private void ShowSprite(Figure figure, byte sheet, ushort sprite) {
        if (sheet == NoSheet || !_viewSheetNames.TryGetValue(sheet, out var name)) {
            return;
        }

        if (figure.Tk != null) {
            if (FindSheet(name, figure.Tk) is not { } collection || sprite >= collection.Count) {
                return;
            }

            if (figure.Tk.Collection != collection || figure.Tk.spriteId != sprite) {
                figure.Tk.SetSprite(collection, sprite);
                RemoveColliders(figure.Object);
            }

            return;
        }

        if (figure.Sprite != null && sprite == NamedSprite && FindUnitySprite(name, figure.Sprite) is { } unitySprite &&
            figure.Sprite.sprite != unitySprite) {
            figure.Sprite.sprite = unitySprite;
        }
    }

    /// <summary>
    /// Finds a sheet of sprites of the game by its name, once for the room: the one the figure already draws from, or
    /// else any loaded.
    /// </summary>
    private tk2dSpriteCollectionData? FindSheet(string name, tk2dBaseSprite current) {
        if (_sheets.TryGetValue(name, out var known)) {
            return known != null ? known : null;
        }

        tk2dSpriteCollectionData? found = null;
        if (current.Collection is { } own && own != null && own.name == name) {
            found = own;
        } else {
            foreach (var sheet in Resources.FindObjectsOfTypeAll<tk2dSpriteCollectionData>()) {
                if (sheet != null && sheet.name == name) {
                    found = sheet;
                    break;
                }
            }
        }

        _sheets[name] = found;
        return found;
    }

    /// <summary>
    /// Finds a sprite of Unity's own by its name, once for the room: the one the figure already draws, or else any
    /// loaded.
    /// </summary>
    private Sprite? FindUnitySprite(string name, SpriteRenderer current) {
        if (_unitySprites.TryGetValue(name, out var known)) {
            return known != null ? known : null;
        }

        Sprite? found = null;
        if (current.sprite is { } own && own != null && own.name == name) {
            found = own;
        } else {
            foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>()) {
                if (sprite != null && sprite.name == name) {
                    found = sprite;
                    break;
                }
            }
        }

        _unitySprites[name] = found;
        return found;
    }

    /// <summary>
    /// Makes the figure of a part, from this game's own part of the same number, or of a thing that the place threw
    /// out, from a thing of the game with the same name.
    /// </summary>
    private Figure? MakeFigure(int number, string? name) {
        if (_viewRoot == null || _ownParts == null) {
            return null;
        }

        GameObject? look;
        if ((number & ThrownBit) == 0) {
            look = number < _ownParts.Count ? _ownParts[number] : null;
        } else {
            look = name == null ? null : FindThrownLook(name);
        }

        if (look == null) {
            return null;
        }

        var copy = new GameObject(look.name + " (Watched)") { layer = look.layer };
        copy.transform.SetParent(_viewRoot.transform, false);
        var figure = new Figure { Object = copy, Transform = copy.transform, Name = name };

        if (look.TryGetComponent<tk2dBaseSprite>(out var tk) && tk.Collection is { } sheet && sheet != null &&
            look.TryGetComponent<MeshRenderer>(out var mesh)) {
            figure.Tk = tk2dBaseSprite.AddComponent<tk2dSprite>(copy, sheet, Mathf.Max(tk.spriteId, 0));
            figure.Tk.color = tk.color;
            var renderer = copy.GetComponent<MeshRenderer>();
            renderer.sortingLayerID = mesh.sortingLayerID;
            renderer.sortingOrder = mesh.sortingOrder;
        } else if (look.TryGetComponent<SpriteRenderer>(out var original)) {
            var sprite = copy.AddComponent<SpriteRenderer>();
            sprite.sprite = original.sprite;
            sprite.color = original.color;
            sprite.sharedMaterial = original.sharedMaterial;
            sprite.sortingLayerID = original.sortingLayerID;
            sprite.sortingOrder = original.sortingOrder;
            sprite.drawMode = original.drawMode;
            if (original.drawMode != SpriteDrawMode.Simple) {
                sprite.size = original.size;
            }

            figure.Sprite = sprite;
        } else {
            Object.Destroy(copy);
            return null;
        }

        // A sprite of the game's sheets can come with a body to bump into, which a figure must not have
        RemoveColliders(copy);
        return figure;
    }

    /// <summary>
    /// Finds a thing of the game with a name, to draw a thing that the place threw out like: one that is out now, or
    /// the one in the game's files.
    /// </summary>
    private GameObject? FindThrownLook(string name) {
        if (_thrownLooks.TryGetValue(name, out var known)) {
            return known;
        }

        GameObject? found = null;
        foreach (var sprite in Resources.FindObjectsOfTypeAll<tk2dBaseSprite>()) {
            if (sprite != null && NameOf(sprite.gameObject) == name) {
                found = sprite.gameObject;
                break;
            }
        }

        _thrownLooks[name] = found;
        return found;
    }

    /// <summary>
    /// Takes away whatever the game put on a figure to bump into.
    /// </summary>
    private static void RemoveColliders(GameObject figure) {
        foreach (var collider in figure.GetComponents<Collider2D>()) {
            Object.Destroy(collider);
        }

        foreach (var collider in figure.GetComponents<Collider>()) {
            Object.Destroy(collider);
        }
    }

    /// <summary>
    /// Moves the figures and the camera towards where they were last said to be, every frame while a round is watched.
    /// </summary>
    public void UpdateView() {
        if (_viewOf == null) {
            return;
        }

        var blend = 1f - Mathf.Exp(-FollowRate * Time.unscaledDeltaTime);
        foreach (var figure in _figures.Values) {
            if (figure.Object == null || !figure.Object.activeSelf) {
                continue;
            }

            var transform = figure.Transform;
            var position = transform.position;
            transform.position = (figure.Target - position).sqrMagnitude > JumpAfter * JumpAfter
                ? figure.Target
                : Vector3.Lerp(position, figure.Target, blend);
            var turn = Mathf.LerpAngle(transform.eulerAngles.z, figure.TargetTurn, blend);
            transform.rotation = Quaternion.Euler(0f, 0f, turn);
        }

        if (_cameraTarget is { } target) {
            Camera = Camera is { } camera && (target - camera).sqrMagnitude <= JumpAfter * JumpAfter * 16f
                ? Vector2.Lerp(camera, target, blend)
                : target;
        }
    }

    #endregion
}
