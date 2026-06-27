using System;
using System.Runtime.Serialization;

namespace PiaNO;

[Serializable]
public class PiaException : ApplicationException
{
    protected PiaException(SerializationInfo info, StreamingContext context)
        : base(info, context) { }

    public PiaException() { }

    public PiaException(string message) : base(message) { }

    public PiaException(string message, Exception innerException)
        : base(message, innerException) { }
}
