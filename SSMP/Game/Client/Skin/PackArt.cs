using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Skin;

/// <summary>
/// The art of a skin of Customizer that someone wears, loaded image by image off the main thread.
/// </summary>
internal sealed class PackArt {
    /// <summary>
    /// How the name of each texture loaded here begins, which tells it from the game's own.
    /// </summary>
    public const string NamePrefix = "SSMP skin ";

    /// <summary>
    /// The skin.
    /// </summary>
    public readonly SkinPack Pack;

    /// <summary>
    /// Called once all the art asked for has loaded.
    /// </summary>
    private readonly Action<PackArt> _loaded;

    /// <summary>
    /// The loaded textures by sheet and atlas, with null for the atlases that the skin has no art for.
    /// </summary>
    private readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.Ordinal);

    /// <summary>
    /// The images that are loading, by sheet and atlas.
    /// </summary>
    private readonly Dictionary<string, UnityWebRequest> _loading = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the art was let go, after which nothing that finishes loading is kept.
    /// </summary>
    private bool _destroyed;

    public PackArt(SkinPack pack, Action<PackArt> loaded) {
        Pack = pack;
        _loaded = loaded;
    }

    /// <summary>
    /// The texture of the skin for the given atlas, or null if it has none or hasn't loaded it.
    /// </summary>
    public Texture2D? Get(string sheet, string texture) =>
        _textures.TryGetValue(Key(sheet, texture), out var loaded) ? loaded : null;

    /// <summary>
    /// Whether any of the textures loaded here is among the given ones.
    /// </summary>
    public bool HoldsAny(HashSet<Texture> textures) {
        foreach (var texture in _textures.Values) {
            if (texture != null && textures.Contains(texture)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Starts loading the art of the given atlases that isn't loaded yet, and says whether all of it is in.
    /// </summary>
    public bool Load(IEnumerable<(string Sheet, string Texture)> atlases) {
        foreach (var (sheet, texture) in atlases) {
            var key = Key(sheet, texture);
            if (_textures.ContainsKey(key) || _loading.ContainsKey(key)) {
                continue;
            }

            var file = Pack.ArtFile(sheet, texture);
            if (file == null) {
                _textures[key] = null;
                continue;
            }

            Start(key, file);
        }

        // The same atlases are asked for every time, so this is all of them
        return _loading.Count == 0;
    }

    /// <summary>
    /// Starts loading one image. Unity decodes it off the main thread, so a large atlas doesn't stall the game.
    /// </summary>
    private void Start(string key, string file) {
        UnityWebRequest request;
        try {
            var parameters = DownloadedTextureParams.Default;
            parameters.mipmapChain = true;
            parameters.readable = false;
            request = UnityWebRequestTexture.GetTexture(new Uri(file).AbsoluteUri, parameters);
        } catch (Exception e) {
            Logger.Warn($"Could not start loading '{file}', loading it at once: {e.Message}");
            _textures[key] = LoadNow(file);
            return;
        }

        _loading[key] = request;
        request.SendWebRequest().completed += _ => {
            // An atlas let go of meanwhile is no longer waited for, or waits for another loading of it
            var wanted = _loading.TryGetValue(key, out var current) && current == request;
            if (wanted) {
                _loading.Remove(key);
            }

            Texture2D? texture = null;
            if (request.result == UnityWebRequest.Result.Success) {
                texture = DownloadHandlerTexture.GetContent(request);
            } else if (!_destroyed && wanted) {
                Logger.Warn($"Could not load '{file}' in the background ({request.error}), loading it at once");
                texture = LoadNow(file);
            }

            request.Dispose();

            if (_destroyed || !wanted) {
                if (texture != null) {
                    Object.Destroy(texture);
                }

                return;
            }

            if (texture != null) {
                texture.name = NamePrefix + key;
            }

            _textures[key] = texture;

            if (_loading.Count == 0) {
                Logger.Info($"Loaded the art of the skin '{Pack.Name}'");
                _loaded(this);
            }
        };
    }

    /// <summary>
    /// Loads one image on the main thread, as Customizer itself does, for when Unity can't load it otherwise.
    /// </summary>
    private static Texture2D? LoadNow(string file) {
        try {
            var texture = new Texture2D(2, 2);
            if (texture.LoadImage(File.ReadAllBytes(file), true)) {
                return texture;
            }

            Object.Destroy(texture);
        } catch (Exception e) {
            Logger.Warn($"Could not load '{file}': {e.Message}");
        }

        return null;
    }

    /// <summary>
    /// Lets go of the art of the given atlases, loaded or loading, which no character draws any more.
    /// </summary>
    /// <returns>Whether this ended the loading: what was still loading was only art let go of now, so the rest is in,
    /// which the characters waiting for it are not told otherwise.</returns>
    public bool Forget(IEnumerable<(string Sheet, string Texture)> atlases) {
        var stopped = false;
        foreach (var (sheet, texture) in atlases) {
            var key = Key(sheet, texture);
            if (_loading.TryGetValue(key, out var request)) {
                _loading.Remove(key);
                request.Abort();
                stopped = true;
            }

            if (_textures.TryGetValue(key, out var loaded)) {
                _textures.Remove(key);
                if (loaded != null) {
                    Object.Destroy(loaded);
                }
            }
        }

        return stopped && _loading.Count == 0 && !_destroyed;
    }

    /// <summary>
    /// Lets go of the art: stops what is loading and frees the textures.
    /// </summary>
    public void Destroy() {
        _destroyed = true;

        foreach (var request in new List<UnityWebRequest>(_loading.Values)) {
            request.Abort();
        }

        foreach (var texture in _textures.Values) {
            if (texture != null) {
                Object.Destroy(texture);
            }
        }

        _textures.Clear();
    }

    private static string Key(string sheet, string texture) => sheet + "/" + texture;
}
