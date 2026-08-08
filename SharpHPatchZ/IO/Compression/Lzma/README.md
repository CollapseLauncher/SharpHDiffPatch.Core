# Attention
This implementation of LzmaStream has been heavily modified from the original managed-lzma and sharpcompress implementation and is more optimized.
The original implementation can be found [here](https://github.com/adamhathcock/sharpcompress/tree/master/src/SharpCompress/Compressors/LZMA)

## Benchmark result
| Method   | Mean     | Error    | StdDev   | Ratio | Gen0   | Gen1   | Allocated | Alloc Ratio |
|--------- |---------:|---------:|---------:|------:|-------:|-------:|----------:|------------:|
| Lzma2New | 23.75 us | 0.197 us | 0.165 us |  0.72 | 0.1526 |      - |   8.23 KB |        0.17 |
| Lzma2Old | 33.17 us | 0.061 us | 0.054 us |  1.00 | 0.9766 | 0.1221 |  48.52 KB |        1.00 |

Hardware Bench:
- CPU: AMD Ryzen 7 9800X3D
- Memory: G.Skill Ripjaws M5 64GB (32x2) DDR5-6000 CL36-36-36-96

Big credits to the original authors of LZMA compression, managed-lzma and sharpcompress for their work.
- [Adam Hathcock (sharpcompress)](https://github.com/adamhathcock)
- [Igor Pavlov (LZMA compression)](https://sourceforge.net/u/ipavlov/profile/)
- [Tobias Käs (managed-lzma)](https://github.com/weltkante/managed-lzma)