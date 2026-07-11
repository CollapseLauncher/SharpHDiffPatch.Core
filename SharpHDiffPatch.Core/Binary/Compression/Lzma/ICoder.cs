using System;

namespace SharpHDiffPatch.Core.Binary.Compression.Lzma;

/// <summary>
/// The exception that is thrown when an error in input stream occurs during decoding.
/// </summary>
internal class DataErrorException() : Exception("Data Error");

/// <summary>
/// The exception that is thrown when the value of an argument is outside the allowable range.
/// </summary>
internal class InvalidParamException() : Exception("Invalid Parameter");