using System.Buffers.Binary;
using PartsLife.Checks.Storage;

namespace PartsLife.Checks.Tests;

public class NvmeHealthTests
{
    [Fact]
    public void Healthy_nvme_is_parsed_field_by_field()
    {
        var h = NvmeHealth.Parse(Fixtures.HealthyNvme());
        Assert.Equal(0, h.CriticalWarning);
        Assert.Equal(41, h.CompositeTemperatureCelsius);
        Assert.Equal(100, h.AvailableSpare);
        Assert.Equal(10, h.AvailableSpareThreshold);
        Assert.Equal(3, h.PercentageUsed);
        Assert.Equal(61_234_567UL, h.DataUnitsRead);
        Assert.Equal(48_765_432UL, h.DataUnitsWritten);
        Assert.Equal(1_502UL, h.PowerCycles);
        Assert.Equal(21_345UL, h.PowerOnHours);
        Assert.Equal(87UL, h.UnsafeShutdowns);
        Assert.Equal(0UL, h.MediaErrors);
        Assert.Equal(2_017UL, h.ErrorLogEntries);
        Assert.Equal(273 + 52, h.TemperatureSensorsKelvin[1]);
        Assert.False(h.Saturated);
        // 1 単位 = 512,000 バイト
        Assert.Equal(48_765_432 * 512_000.0, h.BytesWritten);
    }

    [Fact]
    public void Failing_nvme_reports_media_errors_and_warning_bits()
    {
        var h = NvmeHealth.Parse(Fixtures.FailingNvme());
        Assert.Equal(363_278UL, h.MediaErrors);
        Assert.Equal(0x05, h.CriticalWarning);
        var names = NvmeHealth.CriticalWarningNames(h.CriticalWarning);
        Assert.Equal(2, names.Count);
        Assert.Contains(names, n => n.Contains("信頼性"));
        Assert.Contains(names, n => n.Contains("予備領域"));
        Assert.Equal(74, h.CompositeTemperatureCelsius);
        Assert.Equal(12u, h.CriticalTempMinutes);
    }

    [Fact]
    public void Short_page_is_rejected_rather_than_read_as_zeros()
    {
        Assert.Throws<ArgumentException>(() => NvmeHealth.Parse(new byte[511]));
    }

    [Fact]
    public void Counters_beyond_64_bits_saturate_and_say_so()
    {
        var b = Fixtures.HealthyNvme();
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(168), 1);   // Media Errors の上位 64 ビット
        var h = NvmeHealth.Parse(b);
        Assert.Equal(ulong.MaxValue, h.MediaErrors);
        Assert.True(h.Saturated);
    }

    [Fact]
    public void Zero_temperature_means_not_reported()
    {
        var b = Fixtures.HealthyNvme();
        b[1] = 0; b[2] = 0;
        Assert.Null(NvmeHealth.Parse(b).CompositeTemperatureCelsius);
    }

    [Fact]
    public void Error_log_skips_empty_slots_and_classifies_status()
    {
        var entries = NvmeErrorEntry.ParseLog(Fixtures.NvmeErrorLog());
        Assert.Equal(2, entries.Count);
        Assert.Equal(2017UL, entries[0].ErrorCount);          // 新しい順
        Assert.True(entries[0].IsMediaError);
        Assert.Equal(0x81, entries[0].StatusCode);
        Assert.Equal(123_456_789UL, entries[0].Lba);
        Assert.False(entries[1].IsMediaError);
        Assert.Equal(0x02, entries[1].StatusCode);
    }
}

public class AtaSmartTests
{
    [Fact]
    public void Attributes_are_read_with_raw_and_thresholds()
    {
        var (d, t) = Fixtures.SataWithPendingSectors();
        var attrs = AtaSmart.Parse(d, t);
        Assert.Equal(9, attrs.Count);
        var pending = attrs.Single(a => a.Id == 197);
        Assert.Equal(8UL, pending.Raw);
        Assert.Equal(200, pending.Current);
        Assert.Equal((byte)0, pending.Threshold);
        var realloc = attrs.Single(a => a.Id == 5);
        Assert.Equal((byte)140, realloc.Threshold);
        Assert.False(realloc.FailingNow);
        Assert.True(realloc.PreFailure);
    }

    [Fact]
    public void Temperature_and_power_on_hours_ignore_vendor_packed_upper_bytes()
    {
        var (d, t) = Fixtures.SataWithPendingSectors();
        var attrs = AtaSmart.Parse(d, t);
        Assert.Equal(38UL, attrs.Single(a => a.Id == 194).Value);
        Assert.Equal(0x2E140026UL, attrs.Single(a => a.Id == 194).Raw);
        Assert.Equal(21_000UL, attrs.Single(a => a.Id == 9).Value);
    }

    [Fact]
    public void Without_thresholds_the_threshold_is_unknown_not_zero()
    {
        var (d, _) = Fixtures.SataWithPendingSectors();
        var attrs = AtaSmart.Parse(d);
        Assert.All(attrs, a => Assert.Null(a.Threshold));
        Assert.All(attrs, a => Assert.False(a.FailingNow));
    }

    [Fact]
    public void Normalized_value_at_or_below_threshold_is_failing()
    {
        var (d, t) = Fixtures.SataFailingThreshold();
        var a = AtaSmart.Parse(d, t).Single(x => x.Id == 5);
        Assert.True(a.FailingNow);
    }
}

public class StorageProtocolTests
{
    [Fact]
    public void Nvme_log_query_has_the_documented_layout()
    {
        var q = StorageProtocol.BuildNvmeLogQuery(0x02, 512);
        Assert.Equal(8 + 40 + 512, q.Length);
        Assert.Equal(50, BinaryPrimitives.ReadInt32LittleEndian(q.AsSpan(0)));    // StorageDeviceProtocolSpecificProperty
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(q.AsSpan(4)));     // PropertyStandardQuery
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(q.AsSpan(8)));     // ProtocolTypeNvme
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(q.AsSpan(12)));    // NVMeDataTypeLogPage
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(16)));  // log page 0x02
        Assert.Equal(40u, BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(24))); // ProtocolDataOffset
        Assert.Equal(512u, BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(28)));// ProtocolDataLength
    }

    [Fact]
    public void Protocol_data_is_extracted_only_when_the_descriptor_is_well_formed()
    {
        var page = Fixtures.HealthyNvme();
        var outp = new byte[8 + 40 + 512];
        BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(0), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(4), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(8 + 16), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(8 + 20), 512);
        page.CopyTo(outp, 48);
        Assert.Equal(page, StorageProtocol.ExtractProtocolData(outp, 512));

        var badVersion = (byte[])outp.Clone();
        badVersion[0] = 47;
        Assert.Null(StorageProtocol.ExtractProtocolData(badVersion, 512));

        var shortLength = (byte[])outp.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(shortLength.AsSpan(8 + 20), 256);
        Assert.Null(StorageProtocol.ExtractProtocolData(shortLength, 512));
    }

    [Fact]
    public void Smart_command_builder_only_accepts_read_features()
    {
        var b = StorageProtocol.BuildSmartRead(StorageProtocol.SmartReadData, 1);
        Assert.Equal(0xD0, b[4]);
        Assert.Equal(0x4F, b[7]);
        Assert.Equal(0xC2, b[8]);
        Assert.Equal(0xB0, b[10]);
        Assert.Equal(1, b[12]);
        // SMART には自己診断の開始（0xD4）や属性の保存（0xD3）もある。組み立てられないこと
        Assert.Throws<ArgumentOutOfRangeException>(() => StorageProtocol.BuildSmartRead(0xD4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => StorageProtocol.BuildSmartRead(0xD3, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => StorageProtocol.BuildSmartRead(0xD6, 0));
    }

    [Fact]
    public void All_zero_smart_table_is_not_treated_as_data()
    {
        Assert.Null(StorageProtocol.ExtractSmart(new byte[StorageProtocol.SendCmdOutSize]));
        var outp = new byte[StorageProtocol.SendCmdOutSize];
        outp[16 + 2] = 5;
        Assert.NotNull(StorageProtocol.ExtractSmart(outp));
        outp[4] = 1;   // bDriverError
        Assert.Null(StorageProtocol.ExtractSmart(outp));
    }

    [Fact]
    public void Device_descriptor_strings_and_bus_are_read()
    {
        var d = Fixtures.DeviceDescriptor("", "Samsung SSD 990 PRO 2TB", "4B2QJXD7", "S7KHNJ0W123456X", 17);
        var id = DeviceIdentity.Parse(d);
        Assert.Equal("Samsung SSD 990 PRO 2TB", id.Model);
        Assert.Equal("S7KHNJ0W123456X", id.Serial);
        Assert.Equal("4B2QJXD7", id.Revision);
        Assert.Equal("", id.Vendor);
        Assert.Equal(BusType.Nvme, id.Bus);
    }

    [Theory]
    [InlineData(BusType.Nvme, "nvme")]
    [InlineData(BusType.Sata, "ata")]
    [InlineData(BusType.Usb, "none")]
    [InlineData(BusType.Raid, "none")]
    [InlineData(BusType.Spaces, "none")]
    public void Unknown_paths_are_never_probed(BusType bus, string expected)
    {
        Assert.Equal(expected, BusNames.ProtocolFor(bus));
    }
}
