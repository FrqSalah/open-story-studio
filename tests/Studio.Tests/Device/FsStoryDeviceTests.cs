using Studio.Device;
using Studio.Device.Fs;
using Studio.Device.Storage;
using Studio.Formats.Crypto;
using Studio.Formats.DeviceFs;

namespace Studio.Tests.Device;

public class FsStoryDeviceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(InMemoryDeviceStorage Storage, FsStoryDevice Device)> V2Async()
    {
        var storage = new InMemoryDeviceStorage();
        await storage.WriteAllBytesAsync(DeviceMetadata.FileName, TestData.V2Metadata(), Ct);
        return (storage, (await FsStoryDevice.TryOpenAsync(storage, cancellationToken: Ct))!);
    }

    [Fact]
    public async Task Storage_without_metadata_is_not_a_device() =>
        Assert.Null(await FsStoryDevice.TryOpenAsync(new InMemoryDeviceStorage(), cancellationToken: Ct));

    [Fact]
    public async Task Reports_device_info()
    {
        var (_, device) = await V2Async();
        var info = await device.GetInfoAsync(Ct);
        Assert.Equal(DeviceGeneration.V2, info.Generation);
        Assert.Equal("2.4", info.Firmware);
        Assert.True(info.IsWriteSupported);
        Assert.NotNull(info.FreeBytes);
    }

    [Fact]
    public async Task Installs_a_scrambled_pack_and_lists_it()
    {
        var (storage, device) = await V2Async();
        var pack = TestData.DeviceReadyMenu(2);
        pack.NightModeAvailable = true;

        await device.InstallAsync(pack, cancellationToken: Ct);

        Assert.Equal([pack.Id], PackIndex.Read(storage.Files[PackIndex.FileName]));
        var folder = $".content/{PackIndex.FolderName(pack.Id)}";
        var clear = FsPackFormat.Write(pack);
        Assert.Equal(clear["ni"], storage.Files[$"{folder}/ni"]);
        Assert.Equal(DeviceCiphers.EncryptCommonKey(clear["ri"]), storage.Files[$"{folder}/ri"]);
        Assert.Equal(DeviceCiphers.EncryptCommonKey(clear["rf/000/00000000"]), storage.Files[$"{folder}/rf/000/00000000"]);
        Assert.True(storage.Files.ContainsKey($"{folder}/bt"));
        Assert.True(storage.Files.ContainsKey($"{folder}/nm"));

        var listed = Assert.Single(await device.ListPacksAsync(Ct));
        Assert.Equal(pack.Id, listed.Id);
        Assert.True(listed.NightMode);
        Assert.True(listed.SizeBytes > 0);
    }

    [Fact]
    public async Task Exported_pack_matches_the_installed_one()
    {
        var (_, device) = await V2Async();
        var pack = TestData.DeviceReadyMenu(3);
        await device.InstallAsync(pack, cancellationToken: Ct);

        var back = await device.ExportAsync(pack.Id, Ct);
        Assert.Equal(pack.Id, back.Id);
        Assert.Equal(pack.StageNodes.Count, back.StageNodes.Count);
        Assert.Equal(pack.StageNodes.Select(s => s.Image?.Hash), back.StageNodes.Select(s => s.Image?.Hash));
        Assert.Equal(pack.StageNodes.Select(s => s.Controls), back.StageNodes.Select(s => s.Controls));
    }

    [Fact]
    public async Task Failed_transfer_leaves_the_device_unchanged()
    {
        var (storage, device) = await V2Async();
        var first = TestData.DeviceReadyMenu(1);
        await device.InstallAsync(first, cancellationToken: Ct);
        var indexBefore = storage.Files[PackIndex.FileName];

        var second = TestData.DeviceReadyMenu(2);
        storage.FailWritesTo = p => p.EndsWith("sf/000/00000000", StringComparison.Ordinal) && p.Contains(PackIndex.FolderName(second.Id), StringComparison.Ordinal);
        await Assert.ThrowsAsync<IOException>(() => device.InstallAsync(second, cancellationToken: Ct));

        Assert.Equal(indexBefore, storage.Files[PackIndex.FileName]);
        Assert.DoesNotContain(storage.Files.Keys, k => k.Contains(PackIndex.FolderName(second.Id), StringComparison.Ordinal));
        Assert.Single(await device.ListPacksAsync(Ct));
    }

    [Fact]
    public async Task Reinstalling_replaces_the_pack()
    {
        var (_, device) = await V2Async();
        var pack = TestData.DeviceReadyMenu(1);
        await device.InstallAsync(pack, cancellationToken: Ct);
        pack.Version = 7;
        await device.InstallAsync(pack, cancellationToken: Ct);

        var listed = Assert.Single(await device.ListPacksAsync(Ct));
        Assert.Equal(7, listed.Version);
    }

    [Fact]
    public async Task Removes_and_reorders_packs()
    {
        var (storage, device) = await V2Async();
        var a = TestData.DeviceReadyMenu(1);
        var b = TestData.DeviceReadyMenu(1);
        var c = TestData.DeviceReadyMenu(1);
        foreach (var p in new[] { a, b, c }) await device.InstallAsync(p, cancellationToken: Ct);

        await device.ReorderAsync([c.Id, a.Id, b.Id], Ct);
        Assert.Equal([c.Id, a.Id, b.Id], (await device.ListPacksAsync(Ct)).Select(p => p.Id));

        await device.RemoveAsync(a.Id, Ct);
        Assert.Equal([c.Id, b.Id], (await device.ListPacksAsync(Ct)).Select(p => p.Id));
        Assert.DoesNotContain(storage.Files.Keys, k => k.Contains(PackIndex.FolderName(a.Id), StringComparison.Ordinal));

        await Assert.ThrowsAsync<DeviceException>(() => device.ReorderAsync([b.Id], Ct));
        await Assert.ThrowsAsync<DeviceException>(() => device.RemoveAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Packs_that_are_not_device_ready_are_rejected_before_writing()
    {
        var (storage, device) = await V2Async();
        var pack = TestData.DeviceReadyMenu(1);
        pack.StageNodes[0].Image = new Studio.Core.Model.MediaAsset(TestData.Png());
        await Assert.ThrowsAsync<DeviceException>(() => device.InstallAsync(pack, cancellationToken: Ct));
        Assert.Single(storage.Files);
    }

    [Fact]
    public async Task Missing_pack_folders_are_flagged()
    {
        var (storage, device) = await V2Async();
        var id = Guid.NewGuid();
        await storage.WriteAllBytesAsync(PackIndex.FileName, PackIndex.Write([id]), Ct);
        var listed = Assert.Single(await device.ListPacksAsync(Ct));
        Assert.False(listed.IsValid);
    }

    [Fact]
    public async Task V3_writes_require_the_experimental_flag()
    {
        var storage = new InMemoryDeviceStorage();
        await storage.WriteAllBytesAsync(DeviceMetadata.FileName, TestData.V3Metadata(), Ct);
        var device = (await FsStoryDevice.TryOpenAsync(storage, cancellationToken: Ct))!;
        Assert.False((await device.GetInfoAsync(Ct)).IsWriteSupported);
        await Assert.ThrowsAsync<DeviceException>(() => device.InstallAsync(TestData.DeviceReadyMenu(1), cancellationToken: Ct));
    }

    [Fact]
    public async Task V3_install_uses_aes_and_the_metadata_boot_file()
    {
        var storage = new InMemoryDeviceStorage();
        await storage.WriteAllBytesAsync(DeviceMetadata.FileName, TestData.V3Metadata(), Ct);
        var device = (await FsStoryDevice.TryOpenAsync(storage, allowExperimentalWrites: true, cancellationToken: Ct))!;
        var pack = TestData.DeviceReadyMenu(2);

        await device.InstallAsync(pack, cancellationToken: Ct);

        var folder = $".content/{PackIndex.FolderName(pack.Id)}";
        var key = device.Metadata.V3Key!;
        Assert.Equal(key.BootFile, storage.Files[$"{folder}/bt"]);
        Assert.Equal(DeviceCiphers.EncryptV3(FsPackFormat.Write(pack)["ri"], key), storage.Files[$"{folder}/ri"]);

        var back = await device.ExportAsync(pack.Id, Ct);
        Assert.Equal(pack.StageNodes.Select(s => s.Image?.Hash), back.StageNodes.Select(s => s.Image?.Hash));
    }

    [Fact]
    public async Task Snapshot_and_restore_the_index()
    {
        var (storage, device) = await V2Async();
        var pack = TestData.DeviceReadyMenu(1);
        await device.InstallAsync(pack, cancellationToken: Ct);
        var snapshot = await device.SnapshotIndexAsync(Ct);

        await device.RemoveAsync(pack.Id, Ct);
        Assert.Empty(await device.ListPacksAsync(Ct));

        await device.RestoreIndexAsync(snapshot, Ct);
        Assert.Equal([pack.Id], PackIndex.Read(storage.Files[PackIndex.FileName]));
    }

    [Fact]
    public async Task Local_storage_works_on_a_real_folder()
    {
        var root = Directory.CreateTempSubdirectory("oss-device-");
        try
        {
            await File.WriteAllBytesAsync(Path.Join(root.FullName, DeviceMetadata.FileName), TestData.V2Metadata(), Ct);
            var device = (await FsStoryDevice.TryOpenAsync(new LocalDeviceStorage(root.FullName), cancellationToken: Ct))!;
            var pack = TestData.DeviceReadyMenu(1);
            await device.InstallAsync(pack, cancellationToken: Ct);
            Assert.True(File.Exists(Path.Join(root.FullName, ".content", PackIndex.FolderName(pack.Id), "ni")));
            Assert.Single(await device.ListPacksAsync(Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new LocalDeviceStorage(root.FullName).ReadAllBytesAsync("../outside", Ct));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
