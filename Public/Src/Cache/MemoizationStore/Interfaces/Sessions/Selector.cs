// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.ContractsLight;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Utils;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Utilities;
using BuildXL.Utilities.Serialization;
using StructUtilities = BuildXL.Cache.ContentStore.Interfaces.Utils.StructUtilities;

namespace BuildXL.Cache.MemoizationStore.Interfaces.Sessions
{
    /// <summary>
    ///     Information combined with a weak fingerprint to yield a strong fingerprint.
    /// </summary>
    public readonly struct Selector : IEquatable<Selector>
    {
        private static readonly ByteArrayComparer ByteComparer = new ByteArrayComparer();

        /// <summary>
        ///     Initializes a new instance of the <see cref="Selector" /> struct.
        /// </summary>
        public Selector(ContentHash contentHash, byte[] output = null)
        {
            ContentHash = contentHash;
            Output = output;
        }

        /// <summary>
        ///     Gets build Engine Input Content Hash.
        /// </summary>
        public ContentHash ContentHash { get; }

        /// <summary>
        ///     Gets build Engine Selector Output, limited to 1kB.
        /// </summary>
        public byte[] Output { get; }

        /// <inheritdoc />
        public bool Equals(Selector other)
        {
            return ContentHash.Equals(other.ContentHash) && ByteArrayComparer.ArraysEqual(Output, other.Output);
        }

        /// <summary>
        ///     Serialize whole value to a binary writer.
        /// </summary>
        public void Serialize(BuildXLWriter writer)
        {
            ContentHash.Serialize(writer);
            ContentHashList.WriteNullableArray(Output, writer);
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Selector" /> struct.
        /// </summary>
        public static Selector Deserialize(BuildXLReader reader)
        {
            Contract.Requires(reader != null);

            var contentHash = new ContentHash(reader);
            var output = ContentHashList.ReadNullableArray(reader);

            return new Selector(contentHash, output);
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Selector" /> struct.
        /// </summary>
        public static Selector Deserialize(ref SpanReader reader)
        {
            var contentHash = ContentHash.FromSpan(reader.ReadSpan(ContentHash.SerializedLength));

            var output = ContentHashList.ReadNullableArray(ref reader);

            return new Selector(contentHash, output);
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return StructUtilities.Equals(this, obj);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return ContentHash.GetHashCode() ^ ByteComparer.GetHashCode(Output);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var hex = Output != null ? HexUtilities.BytesToHex(Output) : string.Empty;
            return $"ContentHash=[{ContentHash}], Output=[{hex}]";
        }

        /// <nodoc />
        public static bool operator ==(Selector left, Selector right)
        {
            return left.Equals(right);
        }

        /// <nodoc />
        public static bool operator !=(Selector left, Selector right)
        {
            return !left.Equals(right);
        }

        /// <summary>
        ///     Create a random value.
        /// </summary>
        public static Selector Random(HashType hashType = HashType.Vso0, int outputLengthBytes = 2)
        {
            byte[] output = outputLengthBytes == 0 ? null : ThreadSafeRandom.GetBytes(outputLengthBytes);
            return new Selector(ContentHash.Random(hashType), output);
        }
    }
}
