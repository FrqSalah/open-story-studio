using System.Buffers.Binary;
using Studio.Core;
using Studio.Core.Model;
using Studio.Device;
using Studio.Device.Raw;
using Studio.Device.Storage;
using Studio.Formats.DeviceFs;
using Studio.Formats.Library;
using Studio.Media;
using Studio.Media.Audio;

namespace Studio.Tests.Device;

/// <summary>In-memory SD card of a v1 device.</summary>
internal sealed class FakeSd : IRawSdAccess
{
    private readonly Dictionary<int, byte[]> _sectors = [];

    public FakeSd()
    {
        // Sector 2: "version" label (UTF-16LE) followed by firmware 1.1 and a card size.
        var s2 = new byte[512];
        var label = "version";
        for (var i = 0; i < label.Length; i++) s2[i * 2] = (byte)label[i];
        s2[16] = 1;
        s2[20] = 1;
        _sectors[2] = s2;
    }

    public int Writes { get; private set; }

    public Task<byte[]> ReadSdAsync(int sector, int count, CancellationToken cancellationToken = default)
    {
        var data = new byte[count * 512];
        for (var i = 0; i < count; i++)
        {
            if (_sectors.TryGetValue(sector + i, out var s)) s.CopyTo(data, i * 512);
        }
        return Task.FromResult(data);
    }

    public Task WriteSdAsync(int sector, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        Writes++;
        for (var i = 0; i < data.Length / 512; i++) _sectors[sector + i] = data.Slice(i * 512, 512).ToArray();
        return Task.CompletedTask;
    }

    public Task<byte[]> ReadSpiAsync(int offset, int count, CancellationToken cancellationToken = default)
    {
        var data = new byte[count * 512];
        BinaryPrimitives.WriteInt64BigEndian(data, 12345678901234);
        return Task.FromResult(data);
    }
}

/// <summary>Records bulk transfers and answers with a successful CSW.</summary>
internal sealed class FakeUsb : IUsbTransport
{
    public List<byte[]> Out { get; } = [];

    public Task<int> BulkOutAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        Out.Add(data.ToArray());
        return Task.FromResult(data.Length);
    }

    public Task<int> BulkInAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 13)
        {
            "USBS"u8.CopyTo(buffer.Span);
            return Task.FromResult(13);
        }
        buffer.Span.Fill(0xAB);
        return Task.FromResult(buffer.Length);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class RawDeviceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StoryPack V1Pack()
    {
        var pack = PackTemplates.SingleStory("v1");
        var bmp = Studio.Media.Images.ImageConverter.ToBmp24(TestData.Png());
        foreach (var s in pack.StageNodes)
        {
            s.Image = new MediaAsset(bmp, MediaTypes.Bmp);
            s.Audio = new MediaAsset(TestData.Wave(), MediaTypes.Wav);
        }
        return pack;
    }

    [Fact]
    public void Command_block_wrapper_layout()
    {
        var cbw = RawSdCard.Cbw([0xf6, 0xe1, 0x00], 100000, 2, inbound: true);
        Assert.Equal(31, cbw.Length);
        Assert.Equal("USBC"u8.ToArray(), cbw[..4]);
        Assert.Equal(1024, BinaryPrimitives.ReadInt32LittleEndian(cbw.AsSpan(8)));
        Assert.Equal(0x80, cbw[12]);
        Assert.Equal(0x10, cbw[14]);
        Assert.Equal(new byte[] { 0xf6, 0xe1, 0x00 }, cbw[15..18]);
        Assert.Equal(100000, BinaryPrimitives.ReadInt32BigEndian(cbw.AsSpan(18)));
        Assert.Equal(2, BinaryPrimitives.ReadInt16BigEndian(cbw.AsSpan(22)));
    }

    [Fact]
    public async Task Sd_read_sends_cbw_then_reads_data_and_status()
    {
        var usb = new FakeUsb();
        using var sd = new RawSdCard(usb);
        var data = await sd.ReadSdAsync(5, 1, Ct);
        Assert.Equal(512, data.Length);
        Assert.All(data, b => Assert.Equal(0xAB, b));
        Assert.Single(usb.Out);
    }

    [Fact]
    public async Task Writes_are_disabled_without_the_experimental_flag()
    {
        var device = new RawStoryDevice(new FakeSd(), "test");
        await Assert.ThrowsAsync<DeviceException>(() => device.InstallAsync(V1Pack(), cancellationToken: Ct));
    }

    [Fact]
    public async Task Installs_lists_and_removes_packs()
    {
        var sd = new FakeSd();
        var device = new RawStoryDevice(sd, "test", allowExperimentalWrites: true);
        var info = await device.GetInfoAsync(Ct);
        Assert.Equal("1.1", info.Firmware);
        Assert.Equal("12345678901234", info.SerialNumber);

        var a = V1Pack();
        var b = V1Pack();
        await device.InstallAsync(a, cancellationToken: Ct);
        await device.InstallAsync(b, cancellationToken: Ct);
        Assert.Equal([a.Id, b.Id], (await device.ListPacksAsync(Ct)).Select(p => p.Id));

        await device.ReorderAsync([b.Id, a.Id], Ct);
        Assert.Equal([b.Id, a.Id], (await device.ListPacksAsync(Ct)).Select(p => p.Id));

        await device.RemoveAsync(b.Id, Ct);
        Assert.Equal([a.Id], (await device.ListPacksAsync(Ct)).Select(p => p.Id));
    }
}

public class DeviceManagerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Converts_backs_up_and_installs_a_library_pack()
    {
        var backups = Directory.CreateTempSubdirectory("oss-backups-");
        try
        {
            var storage = new InMemoryDeviceStorage();
            await storage.WriteAllBytesAsync(DeviceMetadata.FileName, TestData.V2Metadata(), Ct);
            var manager = new DeviceManager(new DeviceAssetPreparer(new PassthroughAudioTranscoder()), new DeviceManagerOptions { BackupFolder = backups.FullName });
            var device = (await manager.OpenAsync(storage, Ct))!;

            var pack = PackTemplates.SingleStory("Depuis la bibliothèque");
            pack.StageNodes[0].Image = new MediaAsset(TestData.Png(), fileName: "cover.png");
            pack.StageNodes[1].Audio = new MediaAsset(TestData.DeviceMp3, fileName: "story.mp3");

            var phases = new List<TransferPhase>();
            await manager.InstallAsync(device, pack, new SyncProgress<TransferProgress>(p => phases.Add(p.Phase)), Ct);

            Assert.Equal([pack.Id], (await device.ListPacksAsync(Ct)).Select(p => p.Id));
            Assert.Contains(TransferPhase.Converting, phases);
            Assert.Equal(TransferPhase.Done, phases[^1]);
            var backup = Assert.Single(manager.ListBackups("23023030000123"));
            Assert.True(File.Exists(Path.Join(backup, "dotmd")));

            await manager.RestoreAsync(device, backup, Ct);
            Assert.Empty(await device.ListPacksAsync(Ct));
        }
        finally
        {
            backups.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Refuses_packs_with_validation_errors()
    {
        var storage = new InMemoryDeviceStorage();
        await storage.WriteAllBytesAsync(DeviceMetadata.FileName, TestData.V2Metadata(), Ct);
        var manager = new DeviceManager(new DeviceAssetPreparer(new PassthroughAudioTranscoder()), new DeviceManagerOptions());
        var device = (await manager.OpenAsync(storage, Ct))!;
        var pack = PackTemplates.SingleStory("x");
        pack.StageNodes[1].Controls = default;

        var e = await Assert.ThrowsAsync<DeviceException>(() => manager.InstallAsync(device, pack, cancellationToken: Ct));
        Assert.Contains("aucune commande", e.Message, StringComparison.Ordinal);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public class LibraryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Folder_library_saves_lists_loads_and_deletes()
    {
        var folder = Directory.CreateTempSubdirectory("oss-library-");
        try
        {
            var library = new FolderPackLibrary(folder.FullName);
            var changes = 0;
            library.Changed += (_, _) => changes++;
            var pack = TestData.DeviceReadyMenu(2);
            pack.Title = "Bibliothèque";

            await library.SaveAsync(pack, Ct);
            var entry = Assert.Single(await library.ListAsync(Ct));
            Assert.Equal("Bibliothèque", entry.Title);
            Assert.NotNull(entry.Thumbnail);

            var loaded = await library.LoadAsync(pack.Id, Ct);
            Assert.Equal(pack.StageNodes.Count, loaded!.StageNodes.Count);

            var exported = await library.ExportAsync(pack.Id, Ct);
            await library.DeleteAsync(pack.Id, Ct);
            Assert.Empty(await library.ListAsync(Ct));

            using var stream = new MemoryStream(exported);
            var imported = await library.ImportAsync(stream, Ct);
            Assert.Equal(pack.Id, imported.Id);
            Assert.Equal(3, changes);
            Assert.Empty(Directory.GetFiles(folder.FullName, "*.tmp"));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task In_memory_library_round_trips()
    {
        var library = new InMemoryPackLibrary();
        var pack = PackTemplates.StoryMenu("Mémoire");
        await library.SaveAsync(pack, Ct);
        Assert.Equal("Mémoire", (await library.LoadAsync(pack.Id, Ct))!.Title);
        Assert.Null(await library.LoadAsync(Guid.NewGuid(), Ct));
    }
}
