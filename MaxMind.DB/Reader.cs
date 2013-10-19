﻿using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MaxMind.DB
{
    /// <summary>
    /// An enumeration specifying the API to use to read the database
    /// </summary>
    public enum FileAccessMode
    {
        MemoryMapped,
        Memory
    }

    /// <summary>
    /// Given a MaxMind DB file, this class will retrieve information about an IP address
    /// </summary>
    public class Reader : IDisposable
    {
        /// <summary>
        /// Gets the metadata.
        /// </summary>
        /// <value>
        /// The metadata.
        /// </value>
        public Metadata Metadata { get; private set; }

        private const int DataSectionSeparatorSize = 16;

        private readonly byte[] _metadataStartMarker = { 0xAB, 0xCD, 0xEF, 77, 97, 120, 77, 105, 110, 100, 46, 99, 111, 109 };

        private readonly string _fileName;

        private int _fileSize;

        private int _ipV4Start;
        private MemoryMappedFile _memoryMappedFile;

        private int IPV4Start
        {
            get
            {
                if (_ipV4Start == 0 || Metadata.IpVersion == 4)
                {
                    int node = 0;
                    for (int i = 0; i < 96 && node < Metadata.NodeCount; i++)
                    {
                        node = ReadNode(node, 0);
                    }
                    _ipV4Start = node;
                }
                return _ipV4Start;
            }
        }

        private ThreadLocal<Stream> _stream { get; set; }

        private Decoder Decoder { get; set; }


        /// <summary>
        /// Initializes a new instance of the <see cref="Reader"/> class.
        /// </summary>
        /// <param name="file">The file.</param>
        public Reader(string file) : this(file, FileAccessMode.MemoryMapped) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="Reader"/> class.
        /// </summary>
        /// <param name="file">The MaxMind DB file.</param>
        /// <param name="mode">The mode by which to access the DB file.</param>
        public Reader(string file, FileAccessMode mode)
        {
            _fileName = file;
            if (mode == FileAccessMode.MemoryMapped)
            {
                var fileInfo = new FileInfo(file);
                var mmfName = fileInfo.FullName.Replace("\\", "-");
                try
                {
                    memoryMappedFile = MemoryMappedFile.OpenExisting(mmfName, MemoryMappedFileRights.Read);
                }
                catch (IOException ex)
                {
                    memoryMappedFile = MemoryMappedFile.CreateFromFile(this.FileName, FileMode.Open, mmfName, fileInfo.Length, MemoryMappedFileAccess.Read);
                }
            }

            _stream = new ThreadLocal<Stream>(() =>
            {
                Stream s;
            if (mode == FileAccessMode.Memory) this.fs = new MemoryStream(File.ReadAllBytes(this.FileName));
                else
                {
                    var fileLength = (int) new FileInfo(file).Length;
                	_memoryMappedFile = MemoryMappedFile.Create(_fileName, MapProtection.PageReadOnly, fileLength);
                    s = _memoryMappedFile.CreateViewStream(0, fileLength);
                }

                return s;
            });

            var start = FindMetadataStart();
            var metaDecode = new Decoder(_stream, start);
            var result = metaDecode.Decode(start);
            Metadata = Deserialize<Metadata>(result.Node);
            Decoder = new Decoder(_stream, Metadata.SearchTreeSize + DataSectionSeparatorSize);
        }

        /// <summary>
        /// Finds the data related to the specified address.
        /// </summary>
        /// <param name="address">The IP address.</param>
        /// <returns>An object containing the IP related data</returns>
        public JToken Find(string address)
        {
            return Find(IPAddress.Parse(address));
        }

        /// <summary>
        /// Finds the data related to the specified address.
        /// </summary>
        /// <param name="address">The IP address.</param>
        /// <returns>An object containing the IP related data</returns>
        public JToken Find(IPAddress address)
        {
            var pointer = FindAddressInTree(address);
            return pointer == 0 ? null : ResolveDataPointer(pointer);
        }

        private JToken ResolveDataPointer(int pointer)
        {
            var resolved = (pointer - Metadata.NodeCount) + Metadata.SearchTreeSize;

            if (resolved >= _stream.Value.Length)
            {
                throw new InvalidDatabaseException(
                        "The MaxMind DB file's search tree is corrupt: "
                                + "contains pointer larger than the database.");
            }

            return Decoder.Decode(resolved).Node;
        }

        private int FindAddressInTree(IPAddress address)
        {
            byte[] rawAddress = address.GetAddressBytes();

            int bitLength = rawAddress.Length * 8;
            int record = StartNode(bitLength);

            for (int i = 0; i < bitLength; i++)
            {
                if (record >= Metadata.NodeCount)
                {
                    break;
                }
                byte b = rawAddress[i / 8];
                int bit = 1 & (b >> 7 - (i % 8));
                record = ReadNode(record, bit);
            }
            if (record == Metadata.NodeCount)
            {
                // record is empty
                return 0;
            }
            else if (record > Metadata.NodeCount)
            {
                // record is a data pointer
                return record;
            }
            throw new InvalidDatabaseException("Something bad happened");
        }

        private int StartNode(int bitLength)
        {
            // Check if we are looking up an IPv4 address in an IPv6 tree. If this
            // is the case, we can skip over the first 96 nodes.
            if (Metadata.IpVersion == 6 && bitLength == 32)
            {
                return IPV4Start;
            }
            // The first node of the tree is always node 0, at the beginning of the
            // value
            return 0;
        }

        private T Deserialize<T>(JToken value)
        {
            var serializer = new JsonSerializer();
            return serializer.Deserialize<T>(new JTokenReader(value));
        }

        private int FindMetadataStart()
        {
            _fileSize = (int)_stream.Value.Length;
            var buffer = new byte[_metadataStartMarker.Length];

            for (int i = (_fileSize - _metadataStartMarker.Length); i > 0; i--)
            {
                _stream.Value.Seek(i, SeekOrigin.Begin);
                _stream.Value.Read(buffer, 0, buffer.Length);

                if (!buffer.SequenceEqual(_metadataStartMarker))
                    continue;

                return i + _metadataStartMarker.Length;
            }

            throw new InvalidDatabaseException(
                    "Could not find a MaxMind DB metadata marker in this file ("
                            + _fileName + "). Is this a valid MaxMind DB file?");
        }

        private int ReadNode(int nodeNumber, int index)
        {
            var baseOffset = nodeNumber * Metadata.NodeByteSize;

            var size = Metadata.RecordSize;

            if (size == 24)
            {
                byte[] buffer = ReadMany(baseOffset + index * 3, 3);
                return Decoder.DecodeInteger(buffer);
            }
            else if (size == 28)
            {
                byte middle = ReadOne(baseOffset + 3);
                middle = (index == 0) ? (byte)(middle >> 4) : (byte)(0x0F & middle);

                byte[] buffer = ReadMany(baseOffset + index * 4, 3);
                return Decoder.DecodeInteger(middle, buffer);
            }
            else if (size == 32)
            {
                byte[] buffer = ReadMany(baseOffset + index * 4, 4);
                return Decoder.DecodeInteger(buffer);
            }

            throw new InvalidDatabaseException("Unknown record size: "
                    + size);
        }

        private byte ReadOne(int position)
        {
            _stream.Value.Seek(position, SeekOrigin.Begin);
            return (byte)_stream.Value.ReadByte();
        }

        private byte[] ReadMany(int position, int size)
        {            
                var buffer = new byte[size];
                _stream.Value.Seek(position, SeekOrigin.Begin);
                _stream.Value.Read(buffer, 0, buffer.Length);
                return buffer;
            }
        }

        public void Dispose()
        {
            _stream.Dispose();
            if(_memoryMappedFile != null)
                _memoryMappedFile.Dispose();
        }
    }
}