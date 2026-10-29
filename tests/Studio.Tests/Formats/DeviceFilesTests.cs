using System.Buffers.Binary;
using Studio.Formats.DeviceFs;

namespace Studio.Tests.Formats;

public class DeviceFilesTests
{
    [Fact]
    public void Parses_v2_metadata()
    {
        var md = DeviceMetadata.Parse(TestData.V2Metadata());
        Assert.Equal(DeviceGeneration.V2, md.Generation);
        Assert.Equal(3, md.FormatVersion);
        Assert.Equal("2.4", md.Firmware);
        Assert.Equal("23023030000123", md.SerialNumber);
        Assert.Equal(256, md.DeviceUuid!.Length);
        Assert.Null(md.V3Key);
    }

    [Fact]
    public void Missing_v2_serial_is_reported_as_null()
    {
        var md = DeviceMetadata.Parse(DeviceMetadata.CreateV2(2, 1, -1, new byte[256]));
        Assert.Null(md.SerialNumber);
    }

    [Fact]
    public void Parses_v3_metadata_format_7_like_stud_io()
    {
        var raw = TestData.V3Metadata();
        var md = DeviceMetadata.Parse(raw);
        Assert.Equal(DeviceGeneration.V3, md.Generation);
        Assert.Equal("3.2", md.Firmware);
        Assert.Equal("2300123456789A", md.SerialNumber);
        Assert.Equal(raw[0x40..0x50], md.V3Key!.AesKey);
        Assert.Equal(raw[0x50..0x60], md.V3Key.AesIv);
        // v7: boot file = 24 serial bytes + their first 8 bytes.
        Assert.Equal([.. raw[0x1A..0x32], .. raw[0x1A..0x22]], md.V3Key.BootFile);
    }

    [Fact]
    public void Format_6_derives_the_key_from_the_serial()
    {
        var raw = TestData.V3Metadata();
        BinaryPrimitives.WriteInt16LittleEndian(raw, 6);
        var md = DeviceMetadata.Parse(raw);
        var sn = raw[0x1A..0x32];
        Assert.Equal(sn[..16], md.V3Key!.AesKey);
        Assert.Equal([.. sn[16..24], .. sn[..8]], md.V3Key.AesIv);
        Assert.Equal(raw[0x40..0x60], md.V3Key.BootFile);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(42)]
    public void Unsupported_metadata_versions_are_rejected(short version)
    {
        var raw = new byte[0x200];
        BinaryPrimitives.WriteInt16LittleEndian(raw, version);
        Assert.Throws<FormatException>(() => DeviceMetadata.Parse(raw));
    }

    [Fact]
    public void Truncated_metadata_is_rejected() =>
        Assert.Throws<FormatException>(() => DeviceMetadata.Parse(new byte[] { 3, 0, 1, 2 }));

    [Fact]
    public void Pack_index_uses_big_endian_uuid_bytes()
    {
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var pi = PackIndex.Write([id]);
        Assert.Equal(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"), pi);
        Assert.Equal([id], PackIndex.Read(pi));
    }

    [Fact]
    public void Pack_index_round_trips_order()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        Assert.Equal(ids, PackIndex.Read(PackIndex.Write(ids)));
    }

    [Fact]
    public void Folder_name_is_the_last_eight_hex_digits_uppercase() =>
        Assert.Equal("CCDDEEFF", PackIndex.FolderName(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")));
}
