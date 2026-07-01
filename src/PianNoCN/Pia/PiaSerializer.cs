using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;

namespace PiaNO;

public static class PiaSerializer
{
    public static void Deserialize(this PiaFile piaFile, Stream stream)
    {
        if (stream is null)
            throw new ArgumentNullException(nameof(stream));

        var headerBytes = new byte[48];
        stream.Read(headerBytes, 0, headerBytes.Length);
        var headerString = Encoding.Default.GetString(headerBytes);
        piaFile.Header = new PiaHeader(headerString);

        stream.Seek(60, SeekOrigin.Begin);

        string inflatedString;
        using (var zStream = new InflaterInputStream(stream))
        {
            using var sr = new StreamReader(zStream);
            inflatedString = sr.ReadToEnd();
        }

        piaFile.Owner = piaFile;
        DeserializeNode(piaFile, inflatedString);
    }

    public static void DeserializeNode(this PiaNode parent, string nodeString)
    {
        if (nodeString is null)
            throw new ArgumentNullException(nameof(nodeString));

        var dataLines = nodeString.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < dataLines.Length; i++)
        {
            var curLine = dataLines[i];
            if (curLine.IndexOf('=') >= 0)
            {
                var value = DeserializeValue(curLine);
                parent.NodeMap[value.Key] = value.Value;
            }
            else if (curLine.IndexOf('{') >= 0)
            {
                var nodeBuilder = GetNodeInnerData(dataLines, i, out int n);

                var childNode = new PiaNode(nodeBuilder)
                {
                    NodeName = curLine.Trim().TrimEnd('{'),
                    Parent = parent,
                    Owner = parent.Owner
                };

                parent.ChildNodes.Add(childNode);
                i = n - 1;
            }
        }
    }

    static string GetNodeInnerData(string[] dataLines, int i, out int n)
    {
        var bracketCount = 1;
        var nodeBuilder = new StringBuilder();
        n = i + 1;
        while (bracketCount != 0 && n < dataLines.Length)
        {
            string subLine = dataLines[n++];
            bracketCount += subLine.IndexOf('{') >= 0 ? 1 : subLine.IndexOf('}') >= 0 ? -1 : 0;
            if (bracketCount != 0)
                nodeBuilder.AppendLine(subLine);
        }
        return nodeBuilder.ToString();
    }

    static KeyValuePair<string, string> DeserializeValue(string valueString)
    {
        var prop = valueString.TrimEnd('\r', '\n').Split('=');
        if (prop[1].StartsWith("\""))
        {
            prop[0] += "_str";
            prop[1] = prop[1].TrimStart('\"');
        }
        var sb = new StringBuilder();

        for (int i = 1; i < prop.Length; i++)
        {
            sb.Append('=' + prop[i]);
        }
        var str = sb.ToString().Substring(1, sb.Length - 1).Trim();

        return new KeyValuePair<string, string>(prop[0].Trim(), str);
    }

    public static void Serialize(this PiaFile piaFile, Stream stream, bool IsPlotOrTxt = true)
    {
        if (stream is null)
            throw new ArgumentNullException(nameof(stream));

        if (IsPlotOrTxt)
        {
            var headerString = piaFile.Header?.ToString();
            var headerBytes = Encoding.Default.GetBytes(headerString);
            stream.Write(headerBytes, 0, headerBytes.Length);
        }

        var nodeString = piaFile.SerializeNode();
        var nodeBytes = Encoding.Default.GetBytes(nodeString);

        if (IsPlotOrTxt)
        {
            byte[] deflatedBytes;
            uint adler32;

            var deflater = new Deflater(Deflater.DEFAULT_COMPRESSION);
            using (var ms = new MemoryStream())
            {
                var deflateStream = new DeflaterOutputStream(ms, deflater);
                deflateStream.Write(nodeBytes, 0, nodeBytes.Length);
                deflateStream.Finish();
                deflatedBytes = ms.ToArray();
            }
            adler32 = (uint)deflater.Adler;

            var checkSum = new byte[12];
            BitConverter.GetBytes(adler32).CopyTo(checkSum, 0);
            BitConverter.GetBytes(nodeBytes.Length).CopyTo(checkSum, 4);
            BitConverter.GetBytes(deflatedBytes.Length).CopyTo(checkSum, 8);
            stream.Write(checkSum, 0, checkSum.Length);

            stream.Write(deflatedBytes, 0, deflatedBytes.Length);
        }
        else
        {
            stream.Write(nodeBytes, 0, nodeBytes.Length);
        }
        stream.Write(Encoding.Default.GetBytes("\0"), 0, 1);
    }

    public static string SerializeNode(this PiaNode node, int level = 0)
    {
        if (node is null)
            throw new ArgumentNullException(nameof(node));

        var nodeBuilder = new StringBuilder();
        var whiteSpace = new string(' ', level);

        foreach (var value in node.NodeMap)
            nodeBuilder.AppendFormat("{0}{1}\n", whiteSpace, SerializeValue(value));

        foreach (var child in node.ChildNodes)
        {
            nodeBuilder.AppendFormat("{0}{1}{2}\n", whiteSpace, child.NodeName, "{");
            nodeBuilder.Append(SerializeNode(child, level + 1));
            nodeBuilder.AppendFormat("{0}{1}\n", whiteSpace, "}");
        }

        return nodeBuilder.ToString();
    }

    static string SerializeValue(KeyValuePair<string, string> value)
    {
        var valueString = $"{value.Key}={value.Value}";
        return valueString.Replace("_str=", "=\"");
    }

    public static PiaNode Add(this PiaNode parent, string name, string? nodeString = null, bool firstSentence = false)
    {
        var childNode = new PiaNode
        {
            NodeName = name,
            Parent = parent,
            Owner = parent.Owner
        };

        int dataStart = firstSentence ? -1 : 0;

        if (nodeString is not null && !string.IsNullOrEmpty(nodeString))
        {
            string[] lines = nodeString.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0)
            {
                var getNodeInnerData = GetNodeInnerData(lines!, dataStart, out _);
                if (!string.IsNullOrEmpty(getNodeInnerData))
                    childNode.DeserializeNode(getNodeInnerData);
            }
        }
        parent.ChildNodes.Add(childNode);
        return childNode;
    }

    public static void Remove(this PiaNode parent, string name)
    {
        if (parent.ChildNodes is null)
            return;

        for (int i = 0; i < parent.ChildNodes.Count; i++)
        {
            var nt = parent.ChildNodes[i];
            if (nt.NodeName == name)
            {
                parent.ChildNodes.Remove(nt);
            }
        }
    }

    public static void RemoveChildNodes(this PiaNode parent, string[] name)
    {
        if (parent.ChildNodes is null)
            return;

        var pia = new List<PiaNode>();
        foreach (var nt in parent.ChildNodes)
        {
            if (nt.NodeMap is null)
                continue;
            foreach (var va in nt.NodeMap)
            {
                if (Array.IndexOf(name, va.Value) >= 0)
                {
                    pia.Add(nt);
                    break;
                }
            }
        }
        if (pia.Count > 0)
            foreach (var item in pia)
                parent.ChildNodes.Remove(item);
    }
}
