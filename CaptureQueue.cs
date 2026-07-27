namespace WindowAudioRecorder;

/// <summary>
/// Carries captured audio from the WASAPI capture thread to the writer thread as a queue of
/// same-sized blocks, drawn from a pool the writer refills as it drains them.
/// <para>
/// The capture callback must never block. NAudio raises <c>DataAvailable</c> synchronously on its
/// own capture thread and does not release the endpoint buffer until the handler returns, so
/// anything that waits in there — a disk write, a contended lock — leaves Windows to overrun its
/// own buffer and discard packets before this app can see them. That loss is invisible: no
/// exception, and no flag NAudio surfaces. So the producer side here only ever copies.
/// </para>
/// <para>
/// In the steady state the writer hands blocks back as fast as the capture takes them, so two or
/// three blocks circulate and nothing is allocated. Falling behind grows the pool instead of
/// dropping audio; catching up releases the surplus back to the heap. Memory therefore tracks the
/// backlog that actually happened rather than the worst one imagined up front.
/// </para>
/// <para>
/// Written for one producer and one consumer. The lock is there to keep the queue, the pool and the
/// pending count consistent with each other, not to admit more threads.
/// </para>
/// </summary>
public sealed class CaptureQueue
{
    /// <summary>
    /// Blocks kept for reuse once the backlog drains. A few is plenty for the steady state, and the
    /// rest are better off with the GC than resident for the remainder of the session.
    /// </summary>
    private const int PooledBlocks = 64;

    private readonly object _gate = new();
    private readonly Stack<byte[]> _pool = new();
    private readonly Queue<(byte[] Block, int Count)> _ready = new();
    private readonly int _blockSize;
    private readonly long _ceilingBytes;

    private byte[]? _filling;
    private int _filled;

    private long _pending;
    private long _peak;
    private long _allocated;

    private volatile bool _exhausted;

    /// <param name="blockSize">
    /// Allocation unit, and the most the writer can be handed at once. Must be a whole number of
    /// audio frames: blocks are consecutive slices of one byte stream, so a block boundary lands on
    /// a frame boundary only if every block is frame-sized, and the pipeline downstream silently
    /// misaligns every remaining sample if one ever is not.
    /// </param>
    /// <param name="ceilingBytes">
    /// A backstop, not the working limit. Growing without bound would eventually take the process
    /// down and lose the whole take, whereas stopping here keeps everything written so far.
    /// </param>
    public CaptureQueue(int blockSize, long ceilingBytes)
    {
        if (blockSize <= 0) throw new ArgumentOutOfRangeException(nameof(blockSize));
        if (ceilingBytes < blockSize) throw new ArgumentOutOfRangeException(nameof(ceilingBytes));

        _blockSize = blockSize;
        _ceilingBytes = ceilingBytes;
    }

    public int BlockSize => _blockSize;

    /// <summary>Bytes captured but not yet handed over, whether sealed or still being packed.</summary>
    public long Pending { get { lock (_gate) { return _pending; } } }

    /// <summary>Deepest the backlog has been since the last <see cref="Reset"/>.</summary>
    public long Peak { get { lock (_gate) { return _peak; } } }

    /// <summary>Memory currently held across the queue and the pool.</summary>
    public long AllocatedBytes { get { lock (_gate) { return _allocated; } } }

    /// <summary>
    /// Latches once a packet has been refused at the ceiling. Latching instead of overwriting is
    /// the point: the caller ends the take and says so rather than quietly gaining a hole.
    /// </summary>
    public bool Exhausted => _exhausted;

    /// <summary>
    /// Packs a captured packet in, taking as many blocks as it needs. Returns false — having
    /// appended nothing at all — only at the memory ceiling, because a partially accepted packet
    /// would splice a gap into the middle of the stream without anything saying so.
    /// </summary>
    public bool Append(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return true;

        lock (_gate)
        {
            if (_pending + data.Length > _ceilingBytes)
            {
                _exhausted = true;
                return false;
            }

            _pending += data.Length;
            if (_pending > _peak) _peak = _pending;

            while (!data.IsEmpty)
            {
                if (_filling is null)
                {
                    // Allocating here means the writer is behind: in the steady state the pool
                    // always has a block waiting. Spending memory at that moment is the whole
                    // bargain — a new block risks a collection, dropping the packet loses audio.
                    _filling = _pool.Count > 0 ? _pool.Pop() : NewBlock();
                    _filled = 0;
                }

                int take = Math.Min(_filling.Length - _filled, data.Length);
                data[..take].CopyTo(_filling.AsSpan(_filled));
                _filled += take;
                data = data[take..];

                if (_filled == _filling.Length) SealFilling();
            }

            return true;
        }
    }

    /// <summary>
    /// Hands the part-packed block over as it stands. The writer calls this before deciding it has
    /// caught up, so that nothing pending means nothing outstanding, and so the block it is waiting
    /// for is never the one still being filled.
    /// </summary>
    public void Seal()
    {
        lock (_gate) { SealFilling(); }
    }

    /// <summary>Takes the oldest sealed block. Return it with <see cref="Recycle"/> when written.</summary>
    public bool TryTake(out byte[] block, out int count)
    {
        lock (_gate)
        {
            if (_ready.Count == 0)
            {
                block = [];
                count = 0;
                return false;
            }

            (block, count) = _ready.Dequeue();
            _pending -= count;
            return true;
        }
    }

    /// <summary>Returns a written block to the pool, or to the heap if the pool is already full.</summary>
    public void Recycle(byte[] block)
    {
        lock (_gate) { Release(block); }
    }

    /// <summary>Empties the queue and starts the statistics over, ready for a new take.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            while (_ready.Count > 0) Release(_ready.Dequeue().Block);
            if (_filling is not null) Release(_filling);

            _filling = null;
            _filled = 0;
            _pending = 0;
            _peak = 0;
            _exhausted = false;
        }
    }

    private void SealFilling()
    {
        if (_filling is null) return;

        if (_filled == 0) Release(_filling);
        else _ready.Enqueue((_filling, _filled));

        _filling = null;
        _filled = 0;
    }

    private void Release(byte[] block)
    {
        if (block.Length == _blockSize && _pool.Count < PooledBlocks) _pool.Push(block);
        else _allocated -= block.Length;
    }

    private byte[] NewBlock()
    {
        _allocated += _blockSize;
        return new byte[_blockSize];
    }
}
