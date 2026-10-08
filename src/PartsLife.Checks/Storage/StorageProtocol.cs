using System.Buffers.Binary;

namespace PartsLife.Checks.Storage;

/// <summary>
/// IOCTL に渡す・受け取るバイト列の組み立てと読み出し。**純粋な関数だけ。**
/// 実際に機器へ投げるのは <see cref="StorageReader"/>。
/// </summary>
public static class StorageProtocol
{
    // STORAGE_PROPERTY_ID / STORAGE_QUERY_TYPE
    public const int StorageDeviceProperty = 0;
    public const int StorageDeviceProtocolSpecificProperty = 50;
    public const int PropertyStandardQuery = 0;

    // STORAGE_PROTOCOL_TYPE / STORAGE_PROTOCOL_NVME_DATA_TYPE
    public const int ProtocolTypeNvme = 3;
    public const int NvmeDataTypeLogPage = 2;

    public const byte NvmeLogErrorInfo = 0x01;
    public const byte NvmeLogHealthInfo = 0x02;

    /// <summary>STORAGE_PROPERTY_QUERY の PropertyId + QueryType（8 バイト）。</summary>
    private const int QueryHeader = 8;
    /// <summary>sizeof(STORAGE_PROTOCOL_SPECIFIC_DATA) = 10 × 4 バイト。</summary>
    public const int ProtocolSpecificDataSize = 40;
    /// <summary>sizeof(STORAGE_PROTOCOL_DATA_DESCRIPTOR) = Version + Size + 上の 40 バイト。</summary>
    public const int ProtocolDataDescriptorSize = 48;

    /// <summary>StorageDeviceProperty を聞く問い合わせ（12 バイト）。</summary>
    public static byte[] BuildDeviceQuery()
    {
        var q = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(q.AsSpan(0), StorageDeviceProperty);
        BinaryPrimitives.WriteInt32LittleEndian(q.AsSpan(4), PropertyStandardQuery);
        return q;
    }

    // ---------------------------------------------------------------------
    // 識別子（基準値の鍵に使う）。どちらも access 0 の handle で通る問い合わせ
    // ---------------------------------------------------------------------
    public const int StorageDeviceIdProperty = 2;

    /// <summary>StorageDeviceIdProperty（VPD 0x83 の識別子の一覧）を聞く問い合わせ（12 バイト）。</summary>
    public static byte[] BuildDeviceIdQuery()
    {
        var q = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(q.AsSpan(0), StorageDeviceIdProperty);
        BinaryPrimitives.WriteInt32LittleEndian(q.AsSpan(4), PropertyStandardQuery);
        return q;
    }

    private const int IdCodeSetBinary = 1;
    private const int IdTypeEui64 = 2;
    private const int IdTypeFcphName = 3;   // NAA（WWN）
    private const int IdAssocDevice = 0;

    /// <summary>
    /// STORAGE_DEVICE_ID_DESCRIPTOR から、**機器そのもの**の一意な識別子を1つ選ぶ。
    ///
    ///   descriptor: 0 Version, 4 Size, 8 NumberOfIdentifiers, 12 Identifiers[]
    ///   identifier: 0 CodeSet, 4 Type, 8 IdentifierSize(2), 10 NextOffset(2), 12 Association, 16 Identifier[]
    ///
    /// EUI-64 / NGUID（NVMe）を先に、無ければ NAA（SATA・SAS の WWN）。
    /// 全部 0 や全部 FF の値は「無い」扱い（そう返す安い機器がある）。何も無ければ空文字。
    /// </summary>
    public static string ParseDeviceUniqueId(ReadOnlySpan<byte> d)
    {
        if (d.Length < 12) return "";
        int size = (int)Math.Min((uint)d.Length, BinaryPrimitives.ReadUInt32LittleEndian(d[4..]));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(d[8..]);
        string eui = "", naa = "";
        int at = 12;
        for (uint i = 0; i < Math.Min(count, 64u) && at + 16 <= size; i++)
        {
            var e = d[at..size];
            int codeSet = BinaryPrimitives.ReadInt32LittleEndian(e);
            int type = BinaryPrimitives.ReadInt32LittleEndian(e[4..]);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(e[8..]);
            int next = BinaryPrimitives.ReadUInt16LittleEndian(e[10..]);
            int assoc = BinaryPrimitives.ReadInt32LittleEndian(e[12..]);
            if (16 + len <= e.Length && codeSet == IdCodeSetBinary && assoc == IdAssocDevice)
            {
                var v = e.Slice(16, len);
                bool junk = v.IndexOfAnyExcept((byte)0) < 0 || v.IndexOfAnyExcept((byte)0xFF) < 0;
                if (!junk && type == IdTypeEui64 && len is 8 or 12 or 16 && eui.Length == 0)
                    eui = "eui." + Convert.ToHexStringLower(v);
                else if (!junk && type == IdTypeFcphName && len is 8 or 16 && naa.Length == 0)
                    naa = "naa." + Convert.ToHexStringLower(v);
            }
            if (next < 16) break;
            at += next;
        }
        return eui.Length > 0 ? eui : naa;
    }

    /// <summary>IOCTL_SCSI_GET_ADDRESS の SCSI_ADDRESS（Length(4), Port, Path, Target, Lun）。</summary>
    public static string ParseScsiAddress(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(d) < 8) return "";
        return $"{d[4]}:{d[5]}:{d[6]}:{d[7]}";
    }

    /// <summary>
    /// NVMe の Get Log Page を、Windows の受け口（StorageDeviceProtocolSpecificProperty）経由で頼む。
    /// **読み出しの命令しか組み立てられない形にしてある**（ログページの番号と長さだけを受ける）。
    /// 入出力は同じバッファを使う。
    /// </summary>
    public static byte[] BuildNvmeLogQuery(byte logPage, int length)
    {
        var buf = new byte[QueryHeader + ProtocolSpecificDataSize + length];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s[0..], StorageDeviceProtocolSpecificProperty);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], PropertyStandardQuery);
        var p = s[QueryHeader..];
        BinaryPrimitives.WriteInt32LittleEndian(p[0..], ProtocolTypeNvme);        // ProtocolType
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], NvmeDataTypeLogPage);     // DataType
        BinaryPrimitives.WriteUInt32LittleEndian(p[8..], logPage);                // ProtocolDataRequestValue
        BinaryPrimitives.WriteUInt32LittleEndian(p[12..], 0);                     // SubValue（オフセット下位）
        BinaryPrimitives.WriteUInt32LittleEndian(p[16..], ProtocolSpecificDataSize); // ProtocolDataOffset
        BinaryPrimitives.WriteUInt32LittleEndian(p[20..], (uint)length);          // ProtocolDataLength
        // 24 FixedProtocolReturnData, 28 SubValue2, 32 SubValue3, 36 SubValue4 は 0
        return buf;
    }

    /// <summary>
    /// 返ってきた STORAGE_PROTOCOL_DATA_DESCRIPTOR からデータ部分を取り出す。
    /// **形が合わなければ null。** 大きさの違う物を無理に読むと、別の場所を数字として読んでしまう。
    /// </summary>
    public static byte[]? ExtractProtocolData(ReadOnlySpan<byte> output, int expectedLength)
    {
        if (output.Length < ProtocolDataDescriptorSize) return null;
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(output[0..]);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(output[4..]);
        if (version != ProtocolDataDescriptorSize || size != ProtocolDataDescriptorSize) return null;

        var p = output[8..];
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(p[16..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(p[20..]);
        if (offset < ProtocolSpecificDataSize || length < expectedLength) return null;
        long start = 8L + offset;
        if (start + expectedLength > output.Length) return null;
        return output.Slice((int)start, expectedLength).ToArray();
    }

    // ---------------------------------------------------------------------
    // ATA SMART（SMART_RCV_DRIVE_DATA）
    // ---------------------------------------------------------------------
    public const byte SmartReadData = 0xD0;
    public const byte SmartReadThresholds = 0xD1;
    /// <summary>sizeof(SENDCMDINPARAMS) - 1（末尾の bBuffer[1] を除く）。</summary>
    public const int SendCmdInSize = 32;
    /// <summary>sizeof(SENDCMDOUTPARAMS) - 1 + 512。データは 16 バイト目から。</summary>
    public const int SendCmdOutSize = 16 + 512;

    /// <summary>
    /// SMART の読み出し命令。**機能番号は READ DATA / READ THRESHOLDS の2つしか受けない。**
    /// SMART には書き込みや自己診断の開始もあるが、そちらは組み立てられないようにしてある。
    /// </summary>
    public static byte[] BuildSmartRead(byte feature, byte driveNumber)
    {
        if (feature is not (SmartReadData or SmartReadThresholds))
            throw new ArgumentOutOfRangeException(nameof(feature), "only SMART READ DATA / READ THRESHOLDS are allowed");
        var b = new byte[SendCmdInSize + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), 512);  // cBufferSize
        b[4] = feature;          // bFeaturesReg
        b[5] = 1;                // bSectorCountReg
        b[6] = 1;                // bSectorNumberReg
        b[7] = 0x4F;             // bCylLowReg   (SMART の合言葉)
        b[8] = 0xC2;             // bCylHighReg
        b[9] = (byte)(0xA0 | ((driveNumber & 1) << 4));   // bDriveHeadReg
        b[10] = 0xB0;            // bCommandReg = SMART
        b[12] = driveNumber;     // bDriveNumber
        return b;
    }

    /// <summary>SENDCMDOUTPARAMS からデータを取り出す。ドライバが誤りを返していたら null。</summary>
    public static byte[]? ExtractSmart(ReadOnlySpan<byte> output)
    {
        if (output.Length < SendCmdOutSize) return null;
        byte driverError = output[4];
        byte ideError = output[5];
        if (driverError != 0 || ideError != 0) return null;
        var data = output.Slice(16, 512);
        // 全部 0 の表は「読めた」ことにしない（通さない変換基板がこれを返す）
        if (data.IndexOfAnyExcept((byte)0) < 0) return null;
        return data.ToArray();
    }

    /// <summary>STORAGE_PREDICT_FAILURE: PredictFailure(4) + VendorSpecific(512)。</summary>
    public static (bool Predict, byte[]? Data) ExtractPredictFailure(ReadOnlySpan<byte> output)
    {
        if (output.Length < 516) return (false, null);
        bool predict = BinaryPrimitives.ReadUInt32LittleEndian(output) != 0;
        var data = output.Slice(4, 512);
        return (predict, data.IndexOfAnyExcept((byte)0) < 0 ? null : data.ToArray());
    }

    /// <summary>DISK_GEOMETRY_EX: Geometry(24) の後に DiskSize(8)。BytesPerSector は 20 バイト目。</summary>
    public static (long Size, int SectorBytes) ParseGeometryEx(ReadOnlySpan<byte> output)
    {
        if (output.Length < 32) return (0, 0);
        int sector = BinaryPrimitives.ReadInt32LittleEndian(output[20..]);
        long size = BinaryPrimitives.ReadInt64LittleEndian(output[24..]);
        return (size, sector);
    }
}
