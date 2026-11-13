using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

#if !NATIVE_GODOT
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
        private Node _parent;

        public string Name { get; set; }
        public IReadOnlyList<Node> Children => _children;

        public void AddChild(Node child)
        {
            if (child != null)
            {
                _children.Add(child);
                child._parent = this;
            }
        }

        public void RemoveChild(Node child)
        {
            if (_children.Remove(child))
                child._parent = null;
        }

        public Node GetParent() => _parent;
        public IReadOnlyList<Node> GetChildren() => _children;
        public int GetChildCount() => _children.Count;

        public T GetNodeOrNull<T>(string path) where T : Node =>
            _children.FirstOrDefault(child => child.Name == path) as T;

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

        public enum LayoutPreset
        {
            FullRect
        }

        public void SetAnchorsAndOffsetsPreset(LayoutPreset preset)
        {
            AnchorLeft = AnchorTop = 0f;
            AnchorRight = AnchorBottom = 1f;
            Position = default;
            Size = (GetParent() as Control)?.Size ?? default;
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
#endif

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
        public List<object> AwardedRelics { get; } = new();
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
    using MegaCrit.Sts2.Core.Context;
    using MegaCrit.Sts2.Core.Multiplayer.Game;

    public partial class SyntheticTreasureRelic : Control
    {
        public object Model { get; set; }
        public bool UsesLargeIcon { get; set; }
    }

    public partial class NTreasureRoomRelicHolder : Control
    {
        public NTreasureRoomRelicHolder()
        {
            Size = new Vector2(136f, 136f);
#if !TREASURE_HOLDER_LAYOUT_SHAPE
            Relic = new SyntheticTreasureRelic { Scale = Vector2.One * 2f };
            AddChild(Relic);
#endif
#if NATIVE_GODOT
            FocusMode = FocusModeEnum.All;
#endif
        }

        public SyntheticTreasureRelic Relic { get; private set; }
        public int Index { get; set; }
        public int InitializationCount { get; private set; }

        public void Initialize(object relic, IRunState runState)
        {
#if TREASURE_HOLDER_LAYOUT_SHAPE
            // v0.113 creates the Large icon during Initialize, not in the scene.
            Relic = new SyntheticTreasureRelic
            {
                UsesLargeIcon = true,
                Position = new Vector2(68f, 68f),
                Scale = Vector2.One * 2f
            };
            AddChild(Relic);
#endif
            Relic.Model = relic;
            InitializationCount++;
        }
    }

    public partial class NTreasureRoomRelicCollection : Control
    {
#if TREASURE_HOLDER_LAYOUT_SHAPE
        private readonly List<Control> _holderLayouts = new List<Control>();
#else
        private readonly List<NTreasureRoomRelicHolder> _multiplayerHolders = new List<NTreasureRoomRelicHolder>();
#endif
        private List<NTreasureRoomRelicHolder> _holdersInUse = new List<NTreasureRoomRelicHolder>();
#if !LEGACY_TREASURE_SHAPE
        private readonly Control _relicContainer;
#endif
        private readonly IRunState _runState;

        public NTreasureRoomRelicCollection() : this(new SyntheticRunState()) { }

        public NTreasureRoomRelicCollection(IRunState runState)
        {
            _runState = runState;
            var container = new Control { Name = "Container", Size = new Vector2(900f, 580f) };
#if !LEGACY_TREASURE_SHAPE
            _relicContainer = container;
#endif
            AddChild(container);
#if TREASURE_HOLDER_LAYOUT_SHAPE
            // Six authored layouts, not an automatically growing holder list.
            for (var count = 1; count <= 6; count++)
            {
                var layout = new Control { Name = "Layout" + count, Visible = false };
                container.AddChild(layout);
                layout.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                _holderLayouts.Add(layout);
                for (var index = 0; index < count; index++)
                {
                    var holder = CreateAuthoredHolder(index);
                    holder.Position = new Vector2(80f + index * 120f, 100f + index % 2 * 70f);
                    if (count == 1)
                    {
                        holder.Size = Vector2.One * 204f;
                        holder.PivotOffset = holder.Size * 0.5f;
                    }
                    layout.AddChild(holder);
                }
            }
#else
            for (var index = 0; index < 4; index++)
            {
                var holder = CreateAuthoredHolder(index);
                container.AddChild(holder);
                _multiplayerHolders.Add(holder);
            }
#endif
        }

        private static NTreasureRoomRelicHolder CreateAuthoredHolder(int index) => new NTreasureRoomRelicHolder
        {
            Name = "MultiplayerRelicHolder" + (index + 1),
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            PivotOffset = new Vector2(68f, 68f),
            Rotation = 0.01f * (index + 1)
        };

#if TREASURE_HOLDER_LAYOUT_SHAPE
        public IReadOnlyList<Control> HolderLayouts => _holderLayouts;
#else
        public IReadOnlyList<NTreasureRoomRelicHolder> MultiplayerHolders => _multiplayerHolders;
#endif
        public IReadOnlyList<NTreasureRoomRelicHolder> HoldersInUse => _holdersInUse;
        public Control DefaultFocusedControl => _holdersInUse.Count == 0 ? null
            : _holdersInUse[_runState.GetPlayerSlotIndex(LocalContext.GetMe(_runState.Players))];
        public void InitializeRelics() => SimulateVanillaInitializeRelics(RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics.Count);
        public bool AwardsFinished { get; private set; }
        public List<object> ProcessedRelics { get; } = new();

        // Synthetic award consumer: every authoritative result, including a skip, needs its holder.
        public void CompleteAwards(IEnumerable<(object Relic, Player Recipient)> results)
        {
            foreach (var result in results)
            {
                var holder = _holdersInUse.First(candidate => ReferenceEquals(candidate.Relic?.Model, result.Relic));
                if (result.Recipient != null) result.Recipient.AwardedRelics.Add(holder.Relic.Model);
                ProcessedRelics.Add(holder.Relic.Model);
            }
            AwardsFinished = true;
        }

        public void SimulateVanillaInitializeRelics(int visibleCount)
        {
            var relics = RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics;
#if TREASURE_HOLDER_LAYOUT_SHAPE
            if (visibleCount == 0)
            {
                foreach (var layout in _holderLayouts)
                    layout.Visible = false;
                return;
            }
            for (var index = 0; index < _holderLayouts.Count; index++)
            {
                _holderLayouts[index].Visible = index == visibleCount - 1;
                if (_holderLayouts[index].Visible)
                    _holdersInUse = _holderLayouts[index].GetChildren().OfType<NTreasureRoomRelicHolder>().ToList();
            }
            for (var index = 0; index < _holdersInUse.Count; index++)
            {
                _holdersInUse[index].Initialize(relics[index], _runState);
                _holdersInUse[index].Index = index;
            }
#else
            _holdersInUse.Clear();
            foreach (var holder in _multiplayerHolders)
            {
                holder.Visible = _holdersInUse.Count < visibleCount;
                if (holder.Visible)
                    holder.Initialize(relics[_holdersInUse.Count], _runState);
                holder.Index = _holdersInUse.Count;
                _holdersInUse.Add(holder);
            }
#endif
        }
    }

    public partial class NHandImage : Control
    {
        public NHandImage() : this(null, 0) { }

        public NHandImage(Player player, int index)
        {
            Player = player;
            Index = index;
        }

        public Player Player { get; }
        public int Index { get; }
#if NATIVE_GODOT
        public override void _Ready() { }
#else
        public void _Ready() { }
#endif
    }
}

namespace MegaCrit.Sts2.Core.Nodes.Rooms
{
    using Godot;
    using MegaCrit.Sts2.Core.Runs;

    public partial class NRestSiteRoom : Control
    {
        private readonly List<Control> _characterContainers = new List<Control>();
        private readonly IRunState _runState;
        private readonly Control _background = new Control { Name = "BgContainer" };

        public NRestSiteRoom() : this(new SyntheticRunState()) { }

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
        public List<Control> CreatedCharacters { get; } = new();
#if NATIVE_GODOT
        public override void _Ready() { }
#else
        public void _Ready() { }
#endif

        public void SimulateVanillaReady()
        {
            for (var index = 0; index < 4; index++)
                _characterContainers.Add(GetNode<Control>("BgContainer/Character_" + (index + 1)));
            for (var index = 0; index < _runState.Players.Count; index++)
            {
                var character = new Control { Name = "PlayerCharacter" + (index + 1) };
                _characterContainers[index].AddChild(character);
                CreatedCharacters.Add(character);
            }
        }
    }
}
