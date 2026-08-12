using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Godot
{
    public class GodotObject
    {
        public static bool IsInstanceValid(GodotObject value) => value != null;
    }

    public struct Vector2 : IEquatable<Vector2>
    {
        public float X;
        public float Y;

        public Vector2(float x, float y)
        {
            X = x;
            Y = y;
        }

        public static Vector2 One => new Vector2(1f, 1f);
        public static Vector2 operator +(Vector2 left, Vector2 right) =>
            new Vector2(left.X + right.X, left.Y + right.Y);
        public static Vector2 operator -(Vector2 left, Vector2 right) =>
            new Vector2(left.X - right.X, left.Y - right.Y);
        public static Vector2 operator *(Vector2 value, float scale) =>
            new Vector2(value.X * scale, value.Y * scale);
        public bool Equals(Vector2 other) => X.Equals(other.X) && Y.Equals(other.Y);
        public override bool Equals(object value) => value is Vector2 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y);
    }

    public static class Mathf
    {
        public const float Pi = (float)Math.PI;
        public const float Tau = (float)(Math.PI * 2.0);
        public static float Abs(float value) => Math.Abs(value);
        public static float Min(float left, float right) => Math.Min(left, right);
        public static float Max(float left, float right) => Math.Max(left, right);
    }

    public class Node : GodotObject
    {
        private readonly List<Node> _children = new List<Node>();

        public string Name { get; set; }
        public IReadOnlyList<Node> Children => _children;

        public void AddChild(Node child)
        {
            if (child != null)
                _children.Add(child);
        }

        public T GetNode<T>(string path) where T : Node
        {
            Node current = this;
            foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = current._children.FirstOrDefault(child => child.Name == segment)
                    ?? throw new InvalidOperationException("Missing synthetic node: " + path);
            }
            return (T)current;
        }
    }

    public class Control : Node
    {
        public enum MouseFilterEnum
        {
            Stop,
            Pass,
            Ignore
        }

        public bool Visible { get; set; } = true;
        public MouseFilterEnum MouseFilter { get; set; }
        public Vector2 Position { get; set; }
        public Vector2 Size { get; set; }
        public Vector2 Scale { get; set; } = Vector2.One;
        public Vector2 PivotOffset { get; set; }
        public float Rotation { get; set; }
        public float AnchorLeft { get; set; }
        public float AnchorTop { get; set; }
        public float AnchorRight { get; set; }
        public float AnchorBottom { get; set; }
    }
}

namespace HarmonyLib
{
    public sealed class Harmony { }

    public static class AccessTools
    {
        public static FieldInfo Field(Type type, string name) =>
            type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
    }
}

namespace STS2Mobile
{
    internal static class PatchHelper
    {
        public static MethodInfo Method(Type type, string name) =>
            type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        public static void Patch(HarmonyLib.Harmony harmony, Type type, string methodName,
            MethodInfo prefix = null, MethodInfo postfix = null, MethodInfo transpiler = null,
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
        {
        }

        public static void PatchGetter(HarmonyLib.Harmony harmony, Type type, string propertyName, MethodInfo prefix)
        {
        }

        public static void Log(string message) => Console.WriteLine(message);
    }
}

namespace MegaCrit.Sts2.Core.Runs
{
    using MegaCrit.Sts2.Core.Entities.Players;

    public interface IRunState
    {
        IReadOnlyList<Player> Players { get; }
        int GetPlayerSlotIndex(Player player);
    }

    public sealed class SyntheticRunState : IRunState
    {
        private readonly List<Player> _players = new List<Player>();
        public IReadOnlyList<Player> Players => _players;

        public void Add(Player player)
        {
            player.RunState = this;
            _players.Add(player);
        }

        public int GetPlayerSlotIndex(Player player) => _players.IndexOf(player);
    }
}

namespace MegaCrit.Sts2.Core.Entities.Players
{
    using MegaCrit.Sts2.Core.Runs;

    public sealed class Player
    {
        public IRunState RunState { get; set; }
    }
}

namespace MegaCrit.Sts2.Core.Context
{
    using System.Collections.Generic;
    using MegaCrit.Sts2.Core.Entities.Players;

    public static class LocalContext
    {
        public static Player LocalPlayer { get; set; }
        public static Player GetMe(IReadOnlyList<Player> players) => LocalPlayer ?? players.FirstOrDefault();
    }
}

namespace MegaCrit.Sts2.Core.Helpers
{
    using Godot;

    public static class SceneHelper
    {
        public static T Instantiate<T>(string innerPath) where T : Node =>
            (T)Activator.CreateInstance(typeof(T));
    }
}

namespace MegaCrit.Sts2.Core.Multiplayer.Game
{
    using System.Collections.Generic;

    public sealed class TreasureRoomRelicSynchronizer
    {
        public TreasureRoomRelicSynchronizer(int relicCount)
        {
            CurrentRelics = Enumerable.Range(0, relicCount).Select(index => (object)index).ToList();
        }

        public IReadOnlyList<object> CurrentRelics { get; }
    }

    public sealed class RunManager
    {
        public static RunManager Instance { get; set; } = new RunManager();
        public TreasureRoomRelicSynchronizer TreasureRoomRelicSynchronizer { get; set; }
    }
}

namespace MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic
{
    using Godot;
    using MegaCrit.Sts2.Core.Entities.Players;
    using MegaCrit.Sts2.Core.Runs;

    public class NTreasureRoomRelicHolder : Control
    {
        public NTreasureRoomRelicHolder()
        {
            Size = new Vector2(136f, 136f);
        }
    }

    public class NTreasureRoomRelicCollection : Control
    {
        private readonly List<NTreasureRoomRelicHolder> _multiplayerHolders = new List<NTreasureRoomRelicHolder>();
        private readonly List<NTreasureRoomRelicHolder> _holdersInUse = new List<NTreasureRoomRelicHolder>();
        private readonly Control _relicContainer = new Control { Name = "Container", Size = new Vector2(900f, 580f) };
        private readonly IRunState _runState;

        public NTreasureRoomRelicCollection(IRunState runState)
        {
            _runState = runState;
            AddChild(_relicContainer);
            for (var index = 0; index < 4; index++)
            {
                var holder = new NTreasureRoomRelicHolder
                {
                    Name = "MultiplayerRelicHolder" + (index + 1),
                    AnchorLeft = 0.5f,
                    AnchorTop = 0.5f,
                    AnchorRight = 0.5f,
                    AnchorBottom = 0.5f
                };
                _relicContainer.AddChild(holder);
                _multiplayerHolders.Add(holder);
            }
        }

        public IReadOnlyList<NTreasureRoomRelicHolder> MultiplayerHolders => _multiplayerHolders;
        public IReadOnlyList<NTreasureRoomRelicHolder> HoldersInUse => _holdersInUse;
        public Control DefaultFocusedControl => null;
        public void InitializeRelics() { }

        public void SimulateVanillaInitializeRelics(int visibleCount)
        {
            foreach (var holder in _multiplayerHolders)
            {
                holder.Visible = _holdersInUse.Count < visibleCount;
                _holdersInUse.Add(holder);
            }
        }
    }

    public class NHandImage : Control
    {
        public NHandImage(Player player, int index)
        {
            Player = player;
            Index = index;
        }

        public Player Player { get; }
        public int Index { get; }
        public void _Ready() { }
    }
}

namespace MegaCrit.Sts2.Core.Nodes.Rooms
{
    using Godot;
    using MegaCrit.Sts2.Core.Runs;

    public class NRestSiteRoom : Control
    {
        private readonly List<Control> _characterContainers = new List<Control>();
        private readonly IRunState _runState;
        private readonly Control _background = new Control { Name = "BgContainer" };

        public NRestSiteRoom(IRunState runState)
        {
            _runState = runState;
            AddChild(_background);
            for (var index = 0; index < 4; index++)
            {
                _background.AddChild(new Control
                {
                    Name = "Character_" + (index + 1),
                    AnchorLeft = 0.5f,
                    AnchorTop = 0.5f,
                    AnchorRight = 0.5f,
                    AnchorBottom = 0.5f,
                    Scale = Vector2.One * 0.5f
                });
            }
        }

        public IReadOnlyList<Control> CharacterContainers => _characterContainers;
        public void _Ready() { }

        public void SimulateVanillaFixedContainerAppend()
        {
            for (var index = 0; index < 4; index++)
                _characterContainers.Add(GetNode<Control>("BgContainer/Character_" + (index + 1)));
        }
    }
}
