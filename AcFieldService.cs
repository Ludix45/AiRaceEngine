using System.Buffers.Binary;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace AiRaceEngineer;

public sealed record FieldCar(
    int Id, int Flags, int Laps, int Position, int RealtimePosition,
    int BestLapMs, int LastLapMs, int CurrentLapMs,
    float Spline, float SpeedMs, float X, float Y, float Z,
    float VelocityX, float VelocityY, float VelocityZ,
    float Gas, float Brake, float Steer, int Gear, int Rpm,
    string Driver, string Car, string Tyre);

public sealed record AcFieldSnapshot(
    IReadOnlyList<FieldCar> Cars, float TrackLengthMeters, DateTime ReceivedAt);

/// <summary>Reads the read-only field bridge published by the AC Python app.</summary>
public sealed class AcFieldService : IDisposable
{
    private const string MapName = "AiRaceEngineer_AcField";
    private const int Magic = 0x45524941;
    private const int Version = 1;
    private const int HeaderSize = 64;
    private const int CarSize = 288;
    private const int MaxCars = 128;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reader;
    private AcFieldSnapshot _current = new([], 0, DateTime.MinValue);

    public AcFieldService() => _reader = Task.Run(ReadLoopAsync);

    public AcFieldSnapshot Current => Volatile.Read(ref _current);

    private async Task ReadLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                using var view = map.CreateViewAccessor(0, HeaderSize + MaxCars * CarSize, MemoryMappedFileAccess.Read);
                ReadFrame(view);
            }
            catch (FileNotFoundException) { }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (Exception) { }
            try { await Task.Delay(100, _cts.Token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    private void ReadFrame(MemoryMappedViewAccessor view)
    {
        var header = new byte[HeaderSize];
        view.ReadArray(0, header, 0, header.Length);
        var sequence = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
        if (sequence < 0 || (sequence & 1) != 0 ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4)) != Magic ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4)) != Version) return;

        var count = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12, 4)), 0, MaxCars);
        var trackLength = (float)BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(40, 8)));
        var cars = new List<FieldCar>(count);
        var raw = new byte[CarSize];
        for (var i = 0; i < count; i++)
        {
            view.ReadArray(HeaderSize + (long)i * CarSize, raw, 0, raw.Length);
            var flags = Int(raw, 4);
            if ((flags & 1) == 0) continue;
            cars.Add(new FieldCar(
                Int(raw, 0), flags, Int(raw, 8), Int(raw, 12), Int(raw, 16),
                Int(raw, 20), Int(raw, 24), Int(raw, 28),
                ReadSingle(raw, 32), ReadSingle(raw, 36),
                ReadSingle(raw, 40), ReadSingle(raw, 44), ReadSingle(raw, 48),
                ReadSingle(raw, 52), ReadSingle(raw, 56), ReadSingle(raw, 60),
                ReadSingle(raw, 64), ReadSingle(raw, 68), ReadSingle(raw, 72),
                BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(76, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(78, 2)),
                Text(raw, 80, 32), Text(raw, 144, 48), Text(raw, 240, 16)));
        }
        if (view.ReadInt32(8) != sequence) return;
        var snapshot = new AcFieldSnapshot(cars, trackLength, DateTime.Now);
        // The writer publishes one whole frame; replace the immutable reference atomically.
        Volatile.Write(ref _current, snapshot);
    }

    private static int Int(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static float ReadSingle(byte[] bytes, int offset) => BitConverter.Int32BitsToSingle(Int(bytes, offset));
    private static string Text(byte[] bytes, int offset, int chars) =>
        Encoding.Unicode.GetString(bytes, offset, chars * 2).TrimEnd('\0', ' ');

    public void Dispose()
    {
        _cts.Cancel();
        try { _reader.Wait(500); } catch { }
        _cts.Dispose();
    }
}
