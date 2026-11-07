using System.Collections.Concurrent;
namespace WinPalm.Services;

/// Live 16 kHz, signed 16-bit mono PCM for System.Speech's IStream adapter.
internal sealed class PcmAudioStream : Stream
{
  private readonly BlockingCollection<byte[]> chunks = new(64); // about 6 seconds at 100 ms per chunk
  private byte[]? current;
  private int offset, completed;
  private long position;

  internal bool Append(byte[] pcm)
  {
    if (pcm.Length is 0 or > 32000 || (pcm.Length & 1) != 0 || Volatile.Read(ref completed) != 0) return false;
    try { return chunks.TryAdd(pcm); } catch (InvalidOperationException) { return false; }
  }

  internal void Complete()
  {
    if (Interlocked.Exchange(ref completed, 1) == 0) chunks.CompleteAdding();
  }

  public override int Read(byte[] buffer, int start, int count)
  {
    ArgumentNullException.ThrowIfNull(buffer);
    ArgumentOutOfRangeException.ThrowIfNegative(start);
    ArgumentOutOfRangeException.ThrowIfNegative(count);
    if (start > buffer.Length - count) throw new ArgumentException("The audio read exceeds its buffer.");
    var read = 0;
    // A short read means end-of-input to SAPI. Fill across phone packets,
    // blocking until enough audio arrives or Complete drains the final bytes.
    while (read < count)
    {
      if (current is null || offset == current.Length)
      {
        try { current = chunks.Take(); offset = 0; }
        catch (InvalidOperationException) { break; }
      }
      var length = Math.Min(count - read, current.Length - offset);
      Array.Copy(current, offset, buffer, start + read, length);
      offset += length; read += length;
    }
    Interlocked.Add(ref position, read);
    return read;
  }

  public override bool CanRead => true;
  public override bool CanSeek => false;
  public override bool CanWrite => false;
  // System.Speech's SpStreamWrapper reads Length during construction, and
  // Position while reading. A live stream has no known end until completed.
  public override long Length => long.MaxValue;
  public override long Position
  {
    get => Interlocked.Read(ref position);
    set { if (value != Position) throw new NotSupportedException("Live audio cannot be repositioned."); }
  }
  public override long Seek(long offset, SeekOrigin origin)
  {
    if (offset == 0 && (origin == SeekOrigin.Current || (origin == SeekOrigin.Begin && Position == 0))) return Position;
    throw new NotSupportedException("Live audio cannot be repositioned.");
  }
  public override void Flush() { }
  public override void SetLength(long length) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  protected override void Dispose(bool disposing) { if (disposing) Complete(); base.Dispose(disposing); }
}
