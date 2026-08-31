namespace TLOverlay.Core.Capture;

/// <summary>
/// Hands out byte arrays of one exact size and takes them back.
///
/// ArrayPool would be the obvious choice and is wrong here: it rounds a request
/// up to the next power of two, and the one caller this exists for -
/// DataReader.ReadBytes - fills every byte of the array it is handed, so an
/// array larger than the image makes it read past the end of the data and
/// throw.
///
/// The size is whatever was asked for last. Frame size changes when the player
/// resizes the game, which is rare and never in a loop, so keeping arrays of a
/// size nobody wants any more would just be holding megabytes for nothing.
/// </summary>
internal sealed class ExactSizeBufferPool
{
    /// <summary>
    /// Two is enough: the pipeline holds one frame at a time, and the second
    /// covers an on-demand translation overlapping the end of a poll.
    /// </summary>
    private const int MaxRetained = 2;

    private readonly object _gate = new();
    private readonly Stack<byte[]> _free = new();

    private int _size;

    public byte[] Rent(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        lock (_gate)
        {
            if (_size != size)
            {
                _free.Clear();
                _size = size;
            }

            return _free.Count > 0 ? _free.Pop() : new byte[size];
        }
    }

    public void Return(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        lock (_gate)
        {
            if (buffer.Length == _size && _free.Count < MaxRetained)
            {
                _free.Push(buffer);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _free.Clear();
            _size = 0;
        }
    }
}
