using System;

namespace SharpHDiffPatch.Core.Binary.Compression.Lzma;

/// <summary>
/// The exception that is thrown when an error in input stream occurs during decoding.
/// </summary>
internal class LzmaDataErrorException() : Exception("Data Error");

/// <summary>
/// The exception that is thrown when the value of an argument is outside the allowable range.
/// </summary>
internal class LzmaInvalidParamException() : Exception("Invalid Parameter");