using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Studio.Core;
using Studio.Core.Model;
using Studio.Formats.Archive;
using Studio.Formats.Assets;
using Studio.Formats.DeviceFs;
using Studio.Formats.Raw;

namespace Studio.Tests.Formats;

public class FsPackFormatTests
{
    [Fact]
    public void Writes_the_node_index_header()
    {
        var pack = TestData.DeviceReadyMenu(3);
        var ni = FsPackFormat.Write(pack)[FsPackFormat.NodeIndex];

        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(ni));
        Assert.Equal(pack.Version, BinaryPrimitives.ReadInt16LittleEndian(ni.AsSpan(2)));
        Assert.Equal(512, BinaryPrimitives.ReadInt32LittleEndian(ni.AsSpan(4)));
        Assert.Equal(44, BinaryPrimitives.ReadInt32LittleEndian(ni.AsSpan(8)));
        Assert.Equal(pack.StageNodes.Count, BinaryPrimitives.ReadInt32LittleEndian(ni.AsSpan(12)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(ni.AsSpan(16))); // two distinct images
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(ni.AsSpan(20))); // one sound: blank and real are the same file
        Assert.Equal(1, ni[24]);
        Assert.Equal(512 + 44 * pack.StageNodes.Count, ni.Length);
    }

    [Fact]
    public void Writes_asset_indexes_with_backslash_paths()
    {
        var folder = FsPackFormat.Write(TestData.DeviceReadyMenu());
        Assert.Equal("000\\00000000000\\00000001", Encoding.ASCII.GetString(folder[FsPackFormat.ImageIndex]));
        Assert.True(folder.ContainsKey("rf/000/00000000"));
        Assert.True(folder.ContainsKey("rf/000/00000001"));
        Assert.True(folder.ContainsKey("sf/000/00000000"));
    }

    [Fact]
    public void Stage_without_audio_gets_the_silent_track()
    {
        var pack = TestData.DeviceReadyMenu();
        foreach (var s in pack.StageNodes) s.Audio = null;
        var folder = FsPackFormat.Write(pack);
        Assert.Equal(EmbeddedData.BlankMp3, folder["sf/000/00000000"]);
    }

    [Fact]
    public void Round_trips_structure_and_controls()
    {
        var pack = TestData.DeviceReadyMenu(3);
        pack.NightModeAvailable = true;
        var read = FsPackFormat.Read(FsPackFormat.Write(pack), pack.Id);

        Assert.Equal(pack.Id, read.Id);
        Assert.True(read.NightModeAvailable);
        Assert.Equal(pack.StageNodes.Count, read.StageNodes.Count);
        Assert.Equal(pack.ActionNodes.Count, read.ActionNodes.Count);
        for (var i = 0; i < pack.StageNodes.Count; i++)
        {
            var (a, b) = (pack.StageNodes[i], read.StageNodes[i]);
            Assert.Equal(a.Controls, b.Controls);
            Assert.Equal(a.Image?.Hash, b.Image?.Hash);
            Assert.Equal(a.OkTransition?.OptionIndex, b.OkTransition?.OptionIndex);
            Assert.Equal(a.HomeTransition?.OptionIndex, b.HomeTransition?.OptionIndex);
            Assert.Equal(
                a.OkTransition?.Action.Options.Select(o => pack.StageNodes.IndexOf(o)),
                b.OkTransition?.Action.Options.Select(o => read.StageNodes.IndexOf(o)));
        }
        Assert.Equal(EmbeddedData.BlankMp3, read.StageNodes[1].Audio!.Data);
    }

    [Fact]
    public void Shared_action_nodes_are_written_once()
    {
        var pack = TestData.DeviceReadyMenu(3);
        var folder = FsPackFormat.Write(pack);
        var optionCount = pack.ActionNodes.Sum(a => a.Options.Count);
        Assert.Equal(optionCount * 4, folder[FsPackFormat.ListIndex].Length);
    }

    [Fact]
    public void Rejects_images_that_are_not_device_ready()
    {
        var pack = TestData.DeviceReadyMenu();
        pack.StageNodes[0].Image = new MediaAsset(TestData.Png(), MediaTypes.Png);
        var e = Assert.Throws<InvalidDataException>(() => FsPackFormat.Write(pack));
        Assert.Contains("BMP", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_audio_with_id3_tags()
    {
        var pack = TestData.DeviceReadyMenu();
        pack.StageNodes[0].Audio = new MediaAsset([.. "ID3"u8, 3, 0, 0, 0, 0, 0, 0, .. TestData.DeviceMp3], MediaTypes.Mp3);
        Assert.Throws<InvalidDataException>(() => FsPackFormat.Write(pack));
    }

    [Fact]
    public void Missing_files_are_reported()
    {
        var folder = FsPackFormat.Write(TestData.DeviceReadyMenu());
        folder.Remove("rf/000/00000001");
        Assert.Throws<InvalidDataException>(() => FsPackFormat.Read(folder, Guid.NewGuid()));
    }
}

public class ArchivePackFormatTests
{
    [Fact]
    public void Round_trips_a_pack()
    {
        var pack = TestData.DeviceReadyMenu(2);
        pack.Title = "Les aventures";
        pack.Description = "Pour le soir";
        pack.Thumbnail = TestData.Png(64, 48);
        var read = ArchivePackFormat.Read(ArchivePackFormat.Write(pack));

        Assert.Equal(pack.Id, read.Id);
        Assert.Equal("Les aventures", read.Title);
        Assert.Equal("Pour le soir", read.Description);
        Assert.Equal(pack.Thumbnail, read.Thumbnail);
        Assert.Equal(pack.StageNodes.Select(s => (s.Id, s.Name, s.Controls, s.Position, s.Image?.Hash, s.Audio?.Hash)),
                     read.StageNodes.Select(s => (s.Id, s.Name, s.Controls, s.Position, s.Image?.Hash, s.Audio?.Hash)));
        Assert.Equal(pack.ActionNodes.Select(a => (a.Id, a.Name, string.Join(",", a.Options.Select(o => o.Id)))),
                     read.ActionNodes.Select(a => (a.Id, a.Name, string.Join(",", a.Options.Select(o => o.Id)))));
    }

    [Fact]
    public void Stores_each_asset_once_under_its_sha1()
    {
        var pack = TestData.DeviceReadyMenu(3);
        using var zip = new ZipArchive(new MemoryStream(ArchivePackFormat.Write(pack)));
        var assets = zip.Entries.Where(e => e.FullName.StartsWith("assets/", StringComparison.Ordinal)).Select(e => e.FullName).ToList();
        Assert.Equal(3, assets.Count);
        Assert.Contains("assets/" + pack.StageNodes[0].Image!.Hash + ".bmp", assets);
    }

    [Fact]
    public void Reads_a_studio_archive_and_honours_square_one()
    {
        const string json = """
        {
          "format": "v1", "title": "Archive STUdio", "version": 2, "nightModeAvailable": true,
          "stageNodes": [
            { "uuid": "11111111-1111-1111-1111-111111111111", "name": "Histoire", "image": null, "audio": "a.mp3",
              "okTransition": null, "homeTransition": { "actionNode": "menu", "optionIndex": 0 },
              "controlSettings": { "wheel": false, "ok": false, "home": true, "pause": true, "autoplay": true } },
            { "uuid": "22222222-2222-2222-2222-222222222222", "squareOne": true, "name": "Couverture", "image": "c.png", "audio": null,
              "okTransition": { "actionNode": "menu", "optionIndex": 0 }, "homeTransition": null,
              "controlSettings": { "wheel": true, "ok": true, "home": false, "pause": false, "autoplay": false },
              "position": { "x": 10.5, "y": 20 } }
          ],
          "actionNodes": [ { "id": "menu", "name": "Menu", "options": [ "11111111-1111-1111-1111-111111111111" ] } ]
        }
        """;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("story.json").Open())) w.Write(json);
            using (var s = zip.CreateEntry("assets/a.mp3").Open()) s.Write(TestData.DeviceMp3);
            using (var s = zip.CreateEntry("assets/c.png").Open()) s.Write(TestData.Png(32, 24));
        }

        var pack = ArchivePackFormat.Read(ms.ToArray());
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), pack.Id);
        Assert.Equal("Couverture", pack.SquareOne!.Name);
        Assert.Equal(new NodePosition(10, 20), pack.SquareOne.Position);
        Assert.Equal(MediaTypes.Png, pack.SquareOne.Image!.MimeType);
        Assert.Equal(MediaTypes.Mp3, pack.StageNodes[1].Audio!.MimeType);
        Assert.True(pack.NightModeAvailable);
        Assert.Equal(2, pack.Version);
        Assert.Same(pack.StageNodes[1], pack.SquareOne.OkTransition!.Action.Options[0]);
    }

    [Fact]
    public void Rejects_archives_without_story_json()
    {
        using var ms = new MemoryStream();
        using (new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) { }
        Assert.Throws<InvalidDataException>(() => ArchivePackFormat.Read(ms.ToArray()));
    }
}

public class RawPackFormatTests
{
    private static StoryPack V1Pack()
    {
        var pack = PackTemplates.SingleStory("v1");
        var image = new MediaAsset(new byte[54 + 320 * 240 * 3], MediaTypes.Bmp);
        // Minimal 24-bit BMP header so the writer accepts the size.
        "BM"u8.CopyTo(image.Data);
        BinaryPrimitives.WriteInt32LittleEndian(image.Data.AsSpan(18), 320);
        BinaryPrimitives.WriteInt32LittleEndian(image.Data.AsSpan(22), 240);
        BinaryPrimitives.WriteInt16LittleEndian(image.Data.AsSpan(28), 24);
        foreach (var s in pack.StageNodes)
        {
            s.Image = image;
            s.Audio = new MediaAsset(TestData.Wave(), MediaTypes.Wav);
        }
        return pack;
    }

    [Fact]
    public void Writes_header_stage_sectors_and_trailer()
    {
        var pack = V1Pack();
        var data = RawPackFormat.Write(pack);

        Assert.Equal(0, data.Length % 512);
        Assert.Equal(pack.StageNodes.Count, BinaryPrimitives.ReadInt16BigEndian(data));
        Assert.Equal(pack.StageNodes[0].Id, new Guid(data.AsSpan(512, 16), bigEndian: true));
        Assert.Equal(EmbeddedData.V1CheckBytes, data[^512..]);
    }

    [Fact]
    public void Action_sectors_follow_stage_sectors()
    {
        var pack = V1Pack();
        var data = RawPackFormat.Write(pack);
        // Cover OK transition points to action offset 2 (after the 2 stage nodes), 1 option, option 0.
        var cover = data.AsSpan(512);
        Assert.Equal(2, BinaryPrimitives.ReadInt16BigEndian(cover[32..]));
        Assert.Equal(1, BinaryPrimitives.ReadInt16BigEndian(cover[34..]));
        Assert.Equal(0, BinaryPrimitives.ReadInt16BigEndian(cover[36..]));
        // The action sector (file sector 3) lists stage index 1.
        Assert.Equal(1, BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(3 * 512)));
    }

    [Fact]
    public void Rejects_non_wave_audio()
    {
        var pack = V1Pack();
        pack.StageNodes[0].Audio = new MediaAsset(TestData.DeviceMp3, MediaTypes.Mp3);
        Assert.Throws<InvalidDataException>(() => RawPackFormat.Write(pack));
    }

    [Theory]
    [InlineData(32000, 1, true)]
    [InlineData(44100, 1, false)]
    [InlineData(32000, 2, false)]
    public void Checks_wave_format(int rate, int channels, bool ok) =>
        Assert.Equal(ok, RawPackFormat.CheckWave(TestData.Wave(rate, channels)) is null);
}
