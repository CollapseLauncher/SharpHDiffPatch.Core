# Attention
This implementation of BZip2InputStream has been heavily modified from the original SharpZipLib implementation and is more optimized.
The original implementation can be found [here](https://github.com/icsharpcode/SharpZipLib/tree/master/src/ICSharpCode.SharpZipLib/BZip2)

## Benchmark result
File: UnityPlayer.dll.bz2 (The UnityPlayer.dll file was originated from Honkai Impact 3rd (Mainland China server) v8.9.0, compressed with BZip2)
Size: 12,869,917 bytes compressed (30,144,408 bytes uncompressed)
Uncompressed File Hash: 7D0B8178 (CRC32)

| Method                   | Mean     | Error   | StdDev  | Allocated  |
|------------------------- |---------:|--------:|--------:|-----------:|
| BZip2InputStreamModified | 661.7 ms | 3.29 ms | 2.91 ms |    1.38 KB |
| BZip2InputStreamOld      | 738.1 ms | 2.29 ms | 2.15 ms | 5481.11 KB |

Hardware Bench:
- CPU: AMD Ryzen 7 9800X3D
- Memory: G.Skill Ripjaws M5 64GB (32x2) DDR5-6000 CL36-36-36-96

Big credits to the original authors of SharpZipLib for their work on BZip2 decompression.
- [Mike Krüger](http://www.icsharpcode.net/pub/relations/krueger.aspx)
- John Reilly
- David Pierson
- Neil McNeight