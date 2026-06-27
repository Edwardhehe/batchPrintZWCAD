using System;
using System.Linq;

namespace PiaNO;

public class PlotterConfiguration : PiaFile
{
    private const string meta = "meta";

    public string? CanonicalFamily
    {
        get => GetValue(meta, "canonical_family_name_str");
        set => SetValue(meta, "canonical_family_name_str", value);
    }

    public string? CanonicalModel
    {
        get => GetValue(meta, "canonical_model_name_str");
        set => SetValue(meta, "canonical_model_name_str", value);
    }

    public string? DriverPath
    {
        get => GetValue(meta, "driver_pathname_str");
        set => SetValue(meta, "driver_pathname_str", value);
    }

    public string? DriverTagline
    {
        get => GetValue(meta, "driver_tag_line_str");
        set => SetValue(meta, "driver_tag_line_str", value);
    }

    public int DriverType
    {
        get => int.Parse(GetValue(meta, "driver_type"));
        set => SetValue(meta, "driver_type", value.ToString());
    }

    public string? DriverVersion
    {
        get => GetValue(meta, "driver_version_str");
        set => SetValue(meta, "driver_version_str", value);
    }

    public string? LocalizedFamily
    {
        get => GetValue(meta, "localized_family_name_str");
        set => SetValue(meta, "localized_family_name_str", value);
    }

    public string? LocalizedModel
    {
        get => GetValue(meta, "localized_model_name_str");
        set => SetValue(meta, "localized_model_name_str", value);
    }

    public string? ModelBase
    {
        get => GetValue(meta, "user_defined_model_basename_str");
        set => SetValue(meta, "user_defined_model_basename_str", value);
    }

    public string? ModelPath
    {
        get => GetValue(meta, "user_defined_model_pathname_str");
        set => SetValue(meta, "user_defined_model_pathname_str", value);
    }

    public bool PlotToFile
    {
        get => bool.Parse(GetValue(meta, "file_only"));
        set => SetValue(meta, "file_only", value.ToString().ToUpper());
    }

    public bool ShowCustomFirst
    {
        get => bool.Parse(GetValue(meta, "show_custom_first"));
        set => SetValue(meta, "show_custom_first", value.ToString().ToUpper());
    }

    public int ToolkitVersion
    {
        get => int.Parse(GetValue(meta, "toolkit_version"));
        set => SetValue(meta, "toolkit_version", value.ToString());
    }

    public bool TruetypeAsText
    {
        get => bool.Parse(GetValue(meta, "truetype_as_text"));
        set => SetValue(meta, "truetype_as_text", value.ToString().ToUpper());
    }

    public PlotterConfiguration() : base() { }

    public PlotterConfiguration(string fileName) : base(fileName) { }

    public object? GetCustomValue(string name) => GetCustomValue<object>(name);

    public T? GetCustomValue<T>(string key)
    {
        if (!HasChildNodes)
            throw new InvalidOperationException($"{NodeName} has no child nodes");

        var node = this["custom"];
        if (node is null)
            throw new InvalidOperationException($"{NodeName} has no custom node");

        foreach (var child in node)
        {
            if (!child.NodeMap.TryGetValue("name_str", out var name) ||
                !child.NodeMap.TryGetValue("value", out var valueString))
                continue;

            if (!name.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            object? value = null;
            if (valueString.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
                valueString.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            {
                value = bool.Parse(valueString);
            }
            else if (valueString.All(char.IsDigit))
            {
                value = int.Parse(valueString);
            }
            else
            {
                if (double.TryParse(valueString, out var numValue))
                    value = numValue;
                else
                    value = valueString;
            }

            return value is null ? default : (T)value;
        }
        return default;
    }

    public void SetCustomValue(string key, object value)
    {
        if (!HasChildNodes)
            throw new InvalidOperationException($"{NodeName} has no child nodes");

        var node = this["custom"];
        if (node is null)
            throw new InvalidOperationException($"{NodeName} has no custom node");

        foreach (var child in node)
        {
            if (!child.NodeMap.TryGetValue("name_str", out var name) ||
                !child.NodeMap.TryGetValue("value", out var _))
                continue;

            if (!name.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            child.SetValue("value", value.ToString());
        }
    }

    private string? GetValue(string nodeName, string name)
    {
        if (!HasChildNodes)
            return null;

        var node = this[nodeName];
        return node?.NodeMap.TryGetValue(name, out var value) == true ? value : null;
    }

    private void SetValue(string nodeName, string name, string? value)
    {
        if (!HasChildNodes || value is null)
            return;

        var node = this[nodeName];
        if (node?.NodeMap is not null)
            node.NodeMap[name] = value;
    }
}
