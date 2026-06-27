using System;
using System.IO;

namespace PiaNO;

public abstract class PiaFile : PiaNode
{
    public PiaHeader? Header { get; internal set; }

    public string? PiaFileName;

    public string? PiaPath;

    protected PiaFile() : base() { }

    protected PiaFile(string piaPath) : base()
    {
        PiaPath = piaPath;
        Read();
    }

    public void Read(string piaFileName)
    {
        PiaFileName = piaFileName;
        Read();
    }

    void Read()
    {
        if (string.IsNullOrEmpty(PiaPath))
            throw new ArgumentException("Value cannot be null or empty.", nameof(PiaPath));

        if (!File.Exists(PiaPath))
            throw new FileNotFoundException(PiaPath);

        try
        {
            PiaFileName = Path.GetFileName(PiaPath);
            using var inStream = new FileStream(PiaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            this.Deserialize(inStream);
            inStream.Close();
        }
        catch (Exception e)
        {
            throw new FileNotFoundException("读文件失败:" + e.Message);
        }
    }

    public void Saves(string? path = null, bool IsPlotOrTxt = true)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("Value cannot be null or empty.", nameof(path));

        PiaPath = path;
        using var outStream = new FileStream(PiaPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        this.Serialize(outStream, IsPlotOrTxt);
        outStream.Close();
    }

    public override string ToString() => Path.GetFileName(PiaFileName ?? string.Empty);
}
