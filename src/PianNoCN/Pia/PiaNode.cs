using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace PiaNO;

public class PiaNode : ICloneable, IEquatable<PiaNode>, IEnumerable<PiaNode>
{
    protected internal bool HasChildNodes => ChildNodes is { Count: > 0 };
    public string InnerData
    {
        get => PiaSerializer.SerializeNode(this);
        set => SetInnerData(Owner ?? this, value);
    }

    public string NodeName = string.Empty;
    protected internal PiaFile? Owner;
    protected internal PiaNode? Parent;

    public List<PiaNode> ChildNodes = new List<PiaNode>();
    public Dictionary<string, string> NodeMap = new Dictionary<string, string>();


    public PiaNode()
    {
    }

    public PiaNode(string? innerData)
    {
        if (innerData is null)
            throw new ArgumentNullException(nameof(innerData));
        this.DeserializeNode(innerData);
    }

    private void SetInnerData(PiaNode piaNode, string value)
    {
        piaNode.DeserializeNode(value);
    }

    public void ValueChange(string key, string value)
    {
        NodeMap[key] = value;
    }

    public void Clear()
    {
        NodeMap.Clear();
        ChildNodes.Clear();
    }

    public string GetValue(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Value cannot be null or empty.", nameof(key));
        return NodeMap.TryGetValue(key, out var value) ? value : NodeMap[key] = string.Empty;
    }

    public void SetValue(string key, string value)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Value cannot be null or empty.", nameof(key));
        NodeMap[key] = value;
    }

    public static Color? GetColor(string input)
    {
        var colorVal = int.Parse(input);
        return colorVal == -1 ? null : Color.FromArgb(colorVal);
    }

    public override bool Equals(object? obj) => this == obj as PiaNode;

    public bool Equals(PiaNode? b) => this == b;

    public static bool operator !=(PiaNode? a, PiaNode? b) => !(a == b);

    public static bool operator ==(PiaNode? a, PiaNode? b)
    {
        if (b is null)
            return a is null;
        if (a is null)
            return false;
        if (ReferenceEquals(a, b))
            return true;

        return string.Equals(a.NodeName, b.NodeName, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode() => base.GetHashCode();

    public virtual PiaNode? this[string name]
    {
        get
        {
            return ChildNodes is { Count: > 0 }
                ? ChildNodes.FirstOrDefault(n => string.Equals(n.NodeName, name, StringComparison.OrdinalIgnoreCase))
                : null;
        }
    }

    public override string ToString() => NodeName;

    public object Clone() => MemberwiseClone();

    public IEnumerator<PiaNode> GetEnumerator() => ChildNodes.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
