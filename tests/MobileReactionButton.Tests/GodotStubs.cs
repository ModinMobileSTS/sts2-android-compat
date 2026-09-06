namespace Godot;

// Only the scene ownership/visibility contract used by the surface tracker.
public class GodotObject
{
    public static bool IsInstanceValid(GodotObject? value) => value != null;
}

public readonly record struct StringName(string Value) : IDisposable
{
    public static implicit operator StringName(string value) => new(value);
    public void Dispose() { }
}

public class Node : GodotObject
{
    private readonly List<Node> _children = new();
    private SceneTree? _tree;
    private Node? _parent;
    public StringName Name { get; set; } = new("");

    public bool IsInsideTree() => _tree != null;
    public Node? GetParent() => _parent;
    public int GetChildCount() => _children.Count;
    public Node GetChild(int index) => _children[index];
    public bool IsAncestorOf(Node node)
    {
        for (var parent = node._parent; parent != null; parent = parent._parent)
            if (ReferenceEquals(parent, this)) return true;
        return false;
    }

    public void AddChild(Node node)
    {
        node._parent?.RemoveChild(node);
        _children.Add(node);
        node._parent = this;
        if (_tree != null) node.Enter(_tree);
    }

    public void RemoveChild(Node node)
    {
        if (!_children.Remove(node)) return;
        node.Exit();
        node._parent = null;
    }

    internal void Enter(SceneTree tree)
    {
        _tree = tree;
        tree.Added(this);
        foreach (var child in _children) child.Enter(tree);
    }

    private void Exit()
    {
        foreach (var child in _children) child.Exit();
        var tree = _tree;
        _tree = null;
        tree?.Removed(this);
    }
}

public class CanvasItem : Node
{
    public bool Visible { get; set; } = true;
    public bool IsVisibleInTree()
    {
        if (!IsInsideTree()) return false;
        for (Node? node = this; node != null; node = node.GetParent())
            if (node is CanvasItem item && !item.Visible) return false;
        return true;
    }
}

public class SceneTree : GodotObject
{
    public Node Root { get; } = new();
    public event Action<Node>? NodeAdded;
    public event Action<Node>? NodeRemoved;
    public SceneTree() => Root.Enter(this);
    internal void Added(Node node) => NodeAdded?.Invoke(node);
    internal void Removed(Node node) => NodeRemoved?.Invoke(node);
}
