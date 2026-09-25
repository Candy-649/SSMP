using ProtocolCheck.Explore;

namespace ProtocolCheck.Models;

/// <summary>
/// Who runs a room: the server makes the first player in a room its scene host, hands the room to whoever is left when
/// the host leaves, and tells a player entering a room who is already there. Transcribed from <c>ServerManager</c>
/// (<c>OnClientEnterScene</c>, <c>HandlePlayerLeaveScene</c>, <c>OnSceneResyncRequest</c>) and <c>ClientManager</c>
/// (<c>OnSceneChange</c>, <c>OnEnterScene</c>, <c>OnPlayerAlreadyInScene</c>, <c>OnPlayerEnterScene</c>,
/// <c>OnPlayerLeaveScene</c>, <c>OnSceneHostTransfer</c>, <c>OnUpdateSceneResync</c>), with the role flags of
/// <c>EntityManager</c>.
/// <para>
/// A game changes scenes in two steps: the new scene becomes the active one (<c>activeSceneChanged</c>, which sends
/// the leave of the old one), and later the hero is put in place (<c>AfterEnterSceneHeroTransformed</c>, which sends the
/// enter). Each game talks to the server through a channel of its own, which the server handles in the order of the
/// server packet IDs within a packet, keeping one enter, one leave and one resync request per packet.
/// </para>
/// <para>
/// The goal is a settled room: nothing on its way, both games placed in a scene and sure of their role, exactly one
/// scene host in a shared room and each player the host of a room they are alone in, the server agreeing, and each
/// game seeing the other player exactly when they share the room. Walking into another room is a disruption, so a dead
/// end is a state that doesn't settle once both players stop walking.
/// </para>
/// </summary>
/// <param name="walks">How many times the players may walk into another room, together.</param>
/// <param name="sceneStamped">Whether enter data carries its scene, as it does since e55e328. Without it, this finds
/// the frozen partner of a scene the local game already left.</param>
internal sealed class SceneHost(int walks = 3, bool sceneStamped = true) : Model<SceneHost.World> {
    private const int ResyncLimit = 2;

    public override string Name => $"scene host ({walks} walks{(sceneStamped ? "" : ", enters without their scene")})";

    public override string Scope =>
        $"two rooms, both players connected, no deaths; at most {ResyncLimit} resync requests per room (SSMP: 5)";

    #region Messages

    public sealed record EnterScene(string NewSceneName) : Msg {
        public override int Packet => Packets.Server("PlayerEnterScene");
        public override object Slot => "";
    }

    public sealed record LeaveScene(string SceneName) : Msg {
        public override int Packet => Packets.Server("PlayerLeaveScene");
        public override object Slot => "";
    }

    public sealed record SceneResyncRequest : Msg {
        public override int Packet => Packets.Server("SceneResyncRequest");
        public override object Slot => "";
    }

    public sealed record PlayerEnterScene(ushort Id, string SceneName) : Msg {
        public override int Packet => Packets.Client("PlayerEnterScene");
        public override object Slot => Id;
    }

    public sealed record PlayerAlreadyInScene(Seq<ushort> Players, bool SceneHost, string SceneName) : Msg {
        public override int Packet => Packets.Client("PlayerAlreadyInScene");
        public override object Slot => "";
    }

    public sealed record PlayerLeaveScene(ushort Id, string SceneName) : Msg {
        public override int Packet => Packets.Client("PlayerLeaveScene");
        public override object Slot => Id;
    }

    public sealed record SceneHostTransfer(string SceneName, bool Demote) : Msg {
        public override int Packet => Packets.Client("SceneHostTransfer");
        public override object Slot => "";
    }

    #endregion

    public sealed class Game : Rec {
        public string Name = "";
        public ushort Id;

        /// <summary>
        /// The active scene, "" before the first one.
        /// </summary>
        public string Active = "";

        /// <summary>
        /// Whether the hero was put in place in the active scene.
        /// </summary>
        public bool Placed;

        public string _lastScene = "";
        public bool _sceneHostDetermined;
        public bool _sceneResyncDue;
        public int _sceneResyncsAsked;

        /// <summary>
        /// EntityManager's flags.
        /// </summary>
        public bool IsSceneHost;

        public bool _sceneRoleDetermined;

        /// <summary>
        /// The other player's <c>IsInLocalScene</c>, which also puts their avatar in the room.
        /// </summary>
        public bool PartnerInLocalScene;
    }

    /// <summary>
    /// The server's <c>ServerPlayerData</c> of one player.
    /// </summary>
    public sealed class ServerPlayer : Rec {
        public string CurrentScene = "";
        public bool IsSceneHost;
    }

    public sealed class World : Rec {
        public Game A = new() { Name = "A", Id = 0 };
        public Game B = new() { Name = "B", Id = 1 };
        public ServerPlayer ServerA = new();
        public ServerPlayer ServerB = new();
        public Channel FromA = new();
        public Channel FromB = new();
        public Channel ToA = new();
        public Channel ToB = new();
        public int Walks;
    }

    /// <summary>
    /// Both games just loaded into the same room and wait for the hero to be put in place.
    /// </summary>
    public override World Initial() {
        var world = new World { Walks = walks };
        world.A.Active = Scenes[0];
        world.B.Active = Scenes[0];
        return world;
    }

    /// <summary>
    /// A game's own messages to the server go out in a packet of their own each. Sharing one would have the server
    /// handle the enter of the new scene before the leave of the old one (PlayerEnterScene 8 comes before
    /// PlayerLeaveScene 9), which takes the role in the new scene away again, but the leave and the enter are sent
    /// frames apart: in 1112 leaves in the partner's host logs, none was handled after the enter that followed it.
    /// </summary>
    public override void Normalize(World w) {
        w.FromA.Flush();
        w.FromB.Flush();
    }

    private static readonly string[] Scenes = ["S1", "S2"];

    public override void Moves(World w, Moves<World> moves) {
        foreach (var g in new[] { w.A, w.B }) {
            var name = g.Name;
            var from = FromOf(w, g);
            if (from.Sent.Count > 0) {
                moves.Add($"server gets {from.Sent[0]} from {name}", s => ServerHandle(s, GameOf(s, name), FromOf(s, name).Take()));
            }

            if (from.Open.Count > 0) {
                moves.Add($"{name} sends to the server", s => FromOf(s, name).Flush());
            }

            var to = ToOf(w, g);
            if (to.Sent.Count > 0) {
                moves.Add($"{name} gets {to.Sent[0]}", s => ClientHandle(s, GameOf(s, name), ToOf(s, name).Take()));
            }

            if (to.Open.Count > 0) {
                moves.Add($"server sends to {name}", s => ToOf(s, name).Flush());
            }

            if (g.Active != "" && !g.Placed) {
                moves.Add($"{name} is put in place in {g.Active}", s => OnEnterScene(s, GameOf(s, name)));
            }

            if (g._sceneResyncDue && !g._sceneHostDetermined) {
                moves.Add($"{name} asks what is in the room again", s => OnUpdateSceneResync(s, GameOf(s, name)));
            }

            if (w.Walks > 0) {
                foreach (var scene in Scenes) {
                    if (scene != g.Active) {
                        moves.Disrupt($"{name} walks into {scene}", s => {
                            OnSceneChange(s, GameOf(s, name), scene);
                            s.Walks--;
                        });
                    }
                }
            }
        }
    }

    public override bool Goal(World w) {
        if (w.FromA.Pending + w.FromB.Pending + w.ToA.Pending + w.ToB.Pending > 0) {
            return false;
        }

        foreach (var g in new[] { w.A, w.B }) {
            if (g.Active == "" || !g.Placed || !g._sceneHostDetermined || !g._sceneRoleDetermined) {
                return false;
            }
        }

        if (w.ServerA.CurrentScene != w.A.Active || w.ServerB.CurrentScene != w.B.Active) {
            return false;
        }

        if (w.A.Active == w.B.Active) {
            return w.A.IsSceneHost != w.B.IsSceneHost && w.ServerA.IsSceneHost == w.A.IsSceneHost &&
                   w.ServerB.IsSceneHost == w.B.IsSceneHost && w.A.PartnerInLocalScene && w.B.PartnerInLocalScene;
        }

        return w.A.IsSceneHost && w.B.IsSceneHost && w.ServerA.IsSceneHost && w.ServerB.IsSceneHost &&
               !w.A.PartnerInLocalScene && !w.B.PartnerInLocalScene;
    }

    public override string Describe(World w) {
        var parts = new List<string>();
        var shared = w.A.Active == w.B.Active;
        foreach (var g in new[] { w.A, w.B }) {
            if (!g._sceneHostDetermined || !g._sceneRoleDetermined) {
                parts.Add($"{g.Name} never learns its role");
            }

            if (g.PartnerInLocalScene != shared) {
                parts.Add(shared ? $"{g.Name} doesn't see the other player in its room" : $"{g.Name} sees the other player in its room, but they are elsewhere");
            }
        }

        if (shared && w.A.IsSceneHost == w.B.IsSceneHost) {
            parts.Add(w.A.IsSceneHost ? "both games run the shared room" : "nobody runs the shared room");
        }

        if (!shared && (!w.A.IsSceneHost || !w.B.IsSceneHost)) {
            parts.Add("a player alone in a room doesn't run it");
        }

        var a = w.ServerA;
        var b = w.ServerB;
        if (a.CurrentScene != w.A.Active || b.CurrentScene != w.B.Active) {
            parts.Add("the server has a player in the wrong room");
        } else if (shared ? a.IsSceneHost == b.IsSceneHost || a.IsSceneHost != w.A.IsSceneHost : !a.IsSceneHost || !b.IsSceneHost) {
            parts.Add("the server is wrong about who runs a room");
        }

        return parts.Count == 0 ? "settles only after something else happens" : string.Join("; ", parts);
    }

    private static Game GameOf(World w, string name) => name == "A" ? w.A : w.B;

    private static Game Other(World w, Game g) => g.Name == "A" ? w.B : w.A;

    private static Channel FromOf(World w, Game g) => g.Name == "A" ? w.FromA : w.FromB;

    private static Channel FromOf(World w, string name) => name == "A" ? w.FromA : w.FromB;

    private static Channel ToOf(World w, Game g) => g.Name == "A" ? w.ToA : w.ToB;

    private static Channel ToOf(World w, string name) => name == "A" ? w.ToA : w.ToB;

    private static ServerPlayer ServerOf(World w, Game g) => g.Name == "A" ? w.ServerA : w.ServerB;

    #region ClientManager and EntityManager

    /// <summary>
    /// ClientManager.OnSceneChange and EntityManager.OnSceneChanged: the new scene is active, the leave of the old one
    /// goes out, and nothing is known about the new one.
    /// </summary>
    private static void OnSceneChange(World w, Game g, string newScene) {
        var oldScene = g.Active;
        g.PartnerInLocalScene = false;
        g._sceneHostDetermined = false;
        if (oldScene != "" && oldScene == g._lastScene) {
            FromOf(w, g).Put(new LeaveScene(oldScene));
        }

        g._sceneRoleDetermined = false;
        g.Active = newScene;
        g.Placed = false;
    }

    /// <summary>
    /// ClientManager.OnEnterScene, when the hero was put in place.
    /// </summary>
    private static void OnEnterScene(World w, Game g) {
        g.Placed = true;
        g._lastScene = g.Active;
        FromOf(w, g).Put(new EnterScene(g.Active));
        g._sceneResyncDue = true;
        g._sceneResyncsAsked = 0;
    }

    private static void OnUpdateSceneResync(World w, Game g) {
        if (g._sceneResyncsAsked >= ResyncLimit) {
            g._sceneResyncDue = false;
            return;
        }

        g._sceneResyncsAsked++;
        FromOf(w, g).Put(new SceneResyncRequest());
    }

    private void ClientHandle(World w, Game g, Msg message) {
        switch (message) {
            case PlayerAlreadyInScene already:
                OnPlayerAlreadyInScene(w, g, already);
                break;
            case PlayerEnterScene enter:
                if (sceneStamped && enter.SceneName != g.Active) {
                    break;
                }

                OnPlayerEnterScene(g);
                break;
            case PlayerLeaveScene leave:
                // OnPlayerLeaveScene ignores a leave of another scene than the active one
                if (g.Active == leave.SceneName) {
                    g.PartnerInLocalScene = false;
                }

                break;
            case SceneHostTransfer transfer:
                // OnSceneHostTransfer ignores a transfer for another scene than the active one
                if (g.Active != transfer.SceneName) {
                    break;
                }

                if (transfer.Demote) {
                    InitializeSceneClient(g);
                } else {
                    g.IsSceneHost = true;
                }

                break;
        }
    }

    private void OnPlayerAlreadyInScene(World w, Game g, PlayerAlreadyInScene already) {
        if (sceneStamped && already.SceneName != g.Active) {
            return;
        }

        if (already.Players.Count > 0) {
            OnPlayerEnterScene(g);
        }

        if (already.SceneHost) {
            g.IsSceneHost = true;
            g._sceneRoleDetermined = true;
        } else {
            InitializeSceneClient(g);
        }

        g._sceneHostDetermined = true;
        g._sceneResyncDue = false;
    }

    private static void OnPlayerEnterScene(Game g) {
        g.PartnerInLocalScene = true;
    }

    private static void InitializeSceneClient(Game g) {
        g.IsSceneHost = false;
        g._sceneRoleDetermined = true;
    }

    #endregion

    #region ServerManager

    private static void ServerHandle(World w, Game g, Msg message) {
        switch (message) {
            case EnterScene enter:
                ServerOf(w, g).CurrentScene = enter.NewSceneName;
                OnClientEnterScene(w, g, false);
                break;
            case LeaveScene leave:
                HandlePlayerLeaveScene(w, g, leave.SceneName);
                break;
            case SceneResyncRequest:
                if (ServerOf(w, g).CurrentScene != "") {
                    OnClientEnterScene(w, g, true);
                }

                break;
        }
    }

    private static void OnClientEnterScene(World w, Game g, bool resync) {
        var player = ServerOf(w, g);
        var other = Other(w, g);
        var otherPlayer = ServerOf(w, other);
        var players = Seq<ushort>.Empty;
        var alreadyPlayersInScene = false;

        if (otherPlayer.CurrentScene == player.CurrentScene) {
            if (!resync) {
                ToOf(w, other).Put(new PlayerEnterScene(g.Id, player.CurrentScene));
            }

            alreadyPlayersInScene = true;
            players = players.Add(other.Id);
        }

        // LastHostedScene is only ever set by OnPlayerDeath, which nothing sends (SetDeath is never called)
        var makeEnteringPlayerHost = resync ? player.IsSceneHost : !alreadyPlayersInScene;
        if (makeEnteringPlayerHost) {
            player.IsSceneHost = true;
        }

        ToOf(w, g).Put(new PlayerAlreadyInScene(players, makeEnteringPlayerHost, player.CurrentScene));
    }

    private static void HandlePlayerLeaveScene(World w, Game g, string sceneName) {
        var player = ServerOf(w, g);
        if (sceneName.Length == 0) {
            return;
        }

        if (player.CurrentScene == sceneName) {
            player.CurrentScene = "";
        }

        var other = Other(w, g);
        var otherPlayer = ServerOf(w, other);
        var otherInScene = otherPlayer.CurrentScene == sceneName;

        if (player.IsSceneHost && otherInScene) {
            otherPlayer.IsSceneHost = true;
            player.IsSceneHost = false;
            ToOf(w, other).Put(new SceneHostTransfer(sceneName, false));
        }

        if (otherInScene) {
            ToOf(w, other).Put(new PlayerLeaveScene(g.Id, sceneName));
        }

        player.IsSceneHost = false;
    }

    #endregion
}
