// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using BuildXL.Utilities;

namespace BuildXL.Storage.Fingerprints
{
    /// <summary>
    /// Fingerprinting constants and utilities.
    /// </summary>
    public static class FingerprintUtilities
    {
        /// <summary>
        /// Object pool of SHA1 algorithms.
        /// </summary>
        /// <remarks>
        /// Current implementation of BuildXL.ContentStore.Interfaces is far from perfect in terms of allocations.
        /// One common way of using it is to call CreateContentHasher each time the hashing is required. Unfortunately, this creates an internal object pool
        /// by itself that causes a lots of allocations (that supposedly should be avoided all together).
        /// </remarks>
#pragma warning disable SYSLIB0021, CA5350 // Type or member is obsolete. Temporarily suppressing the warning for .net 6. Work item: 1885580, disable CA5350 Do Not Use Weak Cryptographic Algorithms
        private static readonly ObjectPool<SHA1Managed> s_hashers = new ObjectPool<SHA1Managed>(() => new SHA1Managed(), hasher => { return hasher; });
#pragma warning restore SYSLIB0021, CA5350 // Type or member is obsolete
        private const string UnexpectedLengthContractViolationMessage = "Created Fingerprint is of length different from 'FingerprintLength'";

        /// <summary>
        /// Length of all fingerprints.
        /// </summary>
        public const int FingerprintLength = SHA1HashInfo.Length;

        /// <summary>
        /// Value of all zeros.
        /// </summary>
        public static readonly Fingerprint ZeroFingerprint = new Fingerprint(new byte[FingerprintLength]);

        /// <summary>
        /// Create a random Fingerprint value.
        /// </summary>
        public static Fingerprint CreateRandom()
        {
            return Fingerprint.Random(FingerprintLength);
        }

        /// <summary>
        /// Create a Fingerprint with all zeros except the given value in the first byte.
        /// </summary>
        public static Fingerprint CreateSpecialValue(byte first)
        {
            Contract.Requires(first >= 0);

            var hashBytes = new byte[FingerprintLength];
            hashBytes[0] = first;

            return CreateFrom(hashBytes);
        }

        /// <summary>
        /// Create a Fingerprint from the given bytes, which must match exactly with the select type.
        /// </summary>
        public static Fingerprint CreateFrom(byte[] value)
        {
            Contract.Requires(value != null);
            Contract.Requires(value.Length == FingerprintLength, $"Expected length is '{FingerprintLength}' but actual length is '{value.Length}'.");
            Contract.Requires(value.Length == FingerprintLength);

            return new Fingerprint(value);
        }

        /// <summary>
        /// Create a Fingerprint from the given bytes, which must match exactly with the select type.
        /// </summary>
        public static Fingerprint CreateFrom(ArraySegment<byte> value)
        {
            Contract.Requires(value.Count == FingerprintLength);

            return new Fingerprint(buffer: value.Array, length: value.Count, offset: value.Offset);
        }

        /// <summary>
        /// Create a Fingerprint from bytes read from the given reader.
        /// </summary>
        /// <remarks>
        /// @precondition: the length of the 'reader' buffer must be at least 'FingerprintLength'
        /// </remarks>
        public static Fingerprint CreateFrom(BinaryReader reader)
        {
            return new Fingerprint(FingerprintLength, reader);
        }

        /// <summary>
        /// Serialize to binary.
        /// </summary>
        public static void WriteTo(in this Fingerprint fingerprint, BinaryWriter writer)
        {
            Contract.Requires(fingerprint.Length > 0, "Not allowed to serialize empty hash");

            fingerprint.SerializeBytes(writer);
        }

        /// <summary>
        /// Calculate a fingerprint for the given content.
        /// </summary>
        public static Fingerprint Hash(string content)
        {
            Contract.Requires(content != null);

            return Hash(Encoding.UTF8.GetBytes(content));
        }

        /// <summary>
        /// Calculate a fingerprint for the given content.
        /// </summary>
        public static Fingerprint Hash(byte[] content)
        {
            return CreateFrom(HashCore(content));
        }

        /// <summary>
        /// Calculates the hash for the given content.
        /// </summary>
        public static byte[] HashCore(byte[] content)
        {
            Contract.Requires(content != null);

            using (var hasherWrapper = s_hashers.GetInstance())
            {
                return hasherWrapper.Instance.ComputeHash(content);
            }
        }

        private static readonly char[] s_safeFileNameChars =
        {
            // numbers (10)
            '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',

            // letters (22) (only lowercase as windows is case sensitive) Some letters removed to reduce the chance of offensive words occurring.
            'a', 'b', 'c', 'd', 'e', /*'f',*/ 'g', 'h', /*'i',*/ 'j', 'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', /*'s',*/ 't', /*'u',*/ 'v', 'w', 'x', 'y', 'z',
        };

        /// <summary>
        /// Set of legal characters for identifiers in most common languages.
        /// There are 64 chars to use 6-bit encoding.
        /// Make sure that alphanumeric are the lowest values. As to generate valid identifiers, they typically can only start with alphanumeric
        /// </summary>
        private static readonly char[] s_identifierChars =
        {
            // lower case letters (26)
            'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't', 'u', 'v', 'w', 'x', 'y', 'z',

            // upper case letters (26)
            'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z',

            // numbers (10)
            '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',

            // extra chars (2)
            'µ', '_', 
        };

        /// <nodoc />
        public static string FingerprintToFileName(Fingerprint fingerprint)
        {
            return FingerprintToFileName(fingerprint.ToByteArray());
        }

        /// <summary>
        /// Helper to return a short filename that is a valid filename
        /// </summary>
        /// <remarks>
        /// This function is a trade-off between easy filename characters and the size of the path
        ///
        /// We currently use Sha1 which has 20 bytes.
        /// A default of using ToHex would expand each byte to 2 characters
        /// n bytes => m char (base m-root(255^n)) => Math.Ceiling(m * 20 / N)
        ///
        /// 1 bytes => 2 char (base16) => (40 char) // easy to code, each byte matches
        /// 5 bytes => 8 char (base32) => (32 char) // nice on bit boundary
        /// 2 bytes => 3 char (base41) => (30 char) // not on bit-boundary so harder to code
        /// 3 bytes => 4 char (base64) => (28 char) // nice on bit-boundary, but need extended characters like '�', '�', '�', '�' etc.
        /// 4 bytes => 5 char (base85) => (25 char) // not in bit-boundary so harder to code.
        ///
        /// For now the character savings of higher bases is not worth it so we'll stick with base32.
        /// We can extend later
        /// </remarks>
        public static string FingerprintToFileName(byte[] fingerprint)
        {
            Contract.Requires(fingerprint != null);
            Contract.Requires(fingerprint.Length > 0);

            var targetBitLength = 5;
            var inputBitLength = 8;
            var bitMask = ((int)Math.Pow(2, inputBitLength) >> (inputBitLength - targetBitLength)) - 1;

            Contract.Assume(s_safeFileNameChars.Length == Math.Pow(2, targetBitLength));

            int nrOfBytes = fingerprint.Length;

            // Multiply by inputBitLength, to get bit count of source.
            int sourceLengthInBits = nrOfBytes * inputBitLength;

            // We are converting base 256 to base 32, since both
            // are power of 2, this makes the task easier.
            // Every 5 bits are converted to a corresponding digit or
            // a character in converted string (8 bits).
            // Calculate how many converted character will be needed.
            int base32LengthInBits = (sourceLengthInBits / targetBitLength) * inputBitLength;
            if (sourceLengthInBits % targetBitLength != 0)
            {
                // Some left over bits, we will pad them with zero.
                base32LengthInBits += inputBitLength;
            }

            // sourceLengthInBits in must have been a multiple of <inputBitLength>.
            int outputLength = base32LengthInBits / inputBitLength;

            // Allocate the string to store converted characters.
            var output = new StringBuilder(outputLength);

            int i = 0;
            int remainingBits = 0;
            int accumulator = 0;

            // For every <targetBitLength> bits insert a character into the string.
            for (i = 0; i < nrOfBytes; i++)
            {
                accumulator = (accumulator << inputBitLength) | fingerprint[i];
                remainingBits += inputBitLength;

                while (remainingBits >= targetBitLength)
                {
                    remainingBits -= targetBitLength;
                    output.Append(s_safeFileNameChars[(accumulator >> remainingBits) & bitMask]);
                }
            }

            // Some left over bits, pad them with zero and insert one character for them also.
            if (remainingBits > 0)
            {
                output.Append(s_safeFileNameChars[(accumulator << (targetBitLength - remainingBits)) & bitMask]);
            }

            // Return the final base32 string.
            return output.ToString();
        }

        /// <summary>
        /// Converts a 32-bit value into a 6 character string that contains valid identifiers
        /// </summary>
        /// <remarks>
        /// The first character only uses 2 bits, this leaves 4 bits for extra data to encode.
        /// If we add the option to encoded extra bits in the future
        /// </remarks>
        public static string ToIdentifier(uint x, byte additionalBits = 0)
        {
            // we only have space for 4 extra bits to encode
            Contract.Assert(additionalBits >= 0 && additionalBits <= 7);

            var b1 = (x >> 30) | (uint)(additionalBits << 2); 
            var b2 = (x >> 24) & 0b111111;
            var b3 = (x >> 18) & 0b111111;
            var b4 = (x >> 12) & 0b111111;
            var b5 = (x >> 6)  & 0b111111;
            var b6 =  x        & 0b111111;

            return new String(new [] {
                s_identifierChars[b1],
                s_identifierChars[b2],
                s_identifierChars[b3],
                s_identifierChars[b4],
                s_identifierChars[b5],
                s_identifierChars[b6],
            });
        }
    }
}
