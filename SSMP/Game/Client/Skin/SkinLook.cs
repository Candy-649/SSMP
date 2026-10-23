using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace SSMP.Game.Client.Skin;

/// <summary>
/// What a player looks like through skin mods: the skins of Silksong Customizer that dress their character, the colour
/// that Player Color gives their cloak, and the skin mods their game has that no other game can show. It travels as
/// text, one line for each part, and an empty text is the game's own look.
/// </summary>
internal sealed class SkinLook {
    /// <summary>
    /// The colour of the cloak in the game's own art, which Player Color takes as the colour to change.
    /// </summary>
    public static readonly Color PlayerColorOriginal = new Color32(0x79, 0x40, 0x4B, 0xFF);

    /// <summary>
    /// The skins of Silksong Customizer that the player wears, in the order in which Customizer goes through them: an
    /// atlas takes the art of the first of them that has it.
    /// </summary>
    public List<SkinPackRef> Packs { get; } = [];

    /// <summary>
    /// The colour that Player Color gives the cloak of the player, or null if it does not colour them.
    /// </summary>
    public PlayerColorLook? PlayerColor { get; set; }

    /// <summary>
    /// The skin mods that the game of the player has loaded and that no other game can show yet.
    /// </summary>
    public List<string> Unshown { get; } = [];

    /// <summary>
    /// Whether this is the game's own look.
    /// </summary>
    public bool IsEmpty => Packs.Count == 0 && PlayerColor == null && Unshown.Count == 0;

    /// <summary>
    /// The text that this look travels as.
    /// </summary>
    public override string ToString() {
        var builder = new StringBuilder();
        foreach (var pack in Packs) {
            Line("c", pack.Name, pack.Fingerprint);
        }

        if (PlayerColor is { } color) {
            Line(
                "p",
                Number(color.Color.r),
                Number(color.Color.g),
                Number(color.Color.b),
                Number(color.Color.a),
                Number(color.Saturation),
                Number(color.Brightness)
            );
        }

        foreach (var mod in Unshown) {
            Line("u", mod);
        }

        return builder.ToString();

        void Line(params string[] fields) {
            if (builder.Length > 0) {
                builder.Append('\n');
            }

            builder.Append(string.Join("\t", fields));
        }

        static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads a look from the text it travels as. Lines it does not know are left out, so that a newer game can say
    /// more than an older one reads.
    /// </summary>
    public static SkinLook Parse(string? text) {
        var look = new SkinLook();
        if (string.IsNullOrEmpty(text)) {
            return look;
        }

        foreach (var line in text!.Split('\n')) {
            var fields = line.Split('\t');
            switch (fields[0]) {
                case "c" when fields.Length >= 3 && fields[1].Length > 0:
                    look.Packs.Add(new SkinPackRef(fields[1], fields[2]));
                    break;
                case "p" when fields.Length >= 7:
                    if (TryNumbers(fields, 1, 6, out var numbers)) {
                        look.PlayerColor = new PlayerColorLook(
                            new Color(numbers[0], numbers[1], numbers[2], numbers[3]),
                            numbers[4],
                            numbers[5]
                        );
                    }

                    break;
                case "u" when fields.Length >= 2 && fields[1].Length > 0:
                    look.Unshown.Add(fields[1]);
                    break;
            }
        }

        return look;
    }

    /// <summary>
    /// Reads the given number of numbers from the fields, starting at the given one.
    /// </summary>
    private static bool TryNumbers(string[] fields, int start, int count, out float[] numbers) {
        numbers = new float[count];
        for (var i = 0; i < count; i++) {
            if (!float.TryParse(
                    fields[start + i],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out numbers[i]
                ) || float.IsNaN(numbers[i]) || float.IsInfinity(numbers[i])) {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// A skin of Silksong Customizer as one game names it to another: the name of its folder, and a fingerprint of its
/// files that finds it in a game that keeps it in a folder of another name.
/// </summary>
internal readonly record struct SkinPackRef(string Name, string Fingerprint);

/// <summary>
/// The colour that Player Color gives the cloak of a player, with the saturation and brightness it draws them with.
/// </summary>
internal sealed record PlayerColorLook(Color Color, float Saturation, float Brightness);
