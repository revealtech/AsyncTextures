using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StbImageSharp;
using Zomg.AsyncTextures.Types;

namespace Zomg.AsyncTextures
{
    public class StbImageDecoder : IAsyncImageDecoder
    {
        public Task<DecodedImage> DecodeImageAsync(Stream input, CancellationToken cancellationToken = default)
        {
            return Task.Run(async () =>
            {
                var stream = input;
                if (!(stream is MemoryStream))
                {
                    // speed up read by copying to memory
                    stream = new MemoryStream() {Capacity = (int)input.Length};
                    await input.CopyToAsync(stream);
                    stream.Seek(0, SeekOrigin.Begin);
                    input.Close();
                }
                var img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                GC.Collect();
                return new DecodedImage(img.Width, img.Height, img.Data);
            });
        }
    }
}