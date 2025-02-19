using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Experimental.Rendering;
using UnityEngine.UI;
using Zomg.AsyncTextures.Types;
using Zomg.AsyncTextures.Utils;
using Debug = UnityEngine.Debug;
using Object = System.Object;

namespace Zomg.AsyncTextures
{
    /// <summary>
    /// Asynchronous loader for runtime textures using compute shaders.
    /// While you can create multiple instances of the class, it is essentially a singleton. Multiple instances will not share their time slices.
    /// </summary>
    /// <remarks>Note that this class will subscribe to the <see cref="Application.quitting"/> event for clean-up, so you do not necessarily need to dispose yourself.</remarks>
    public class AsyncTextureLoader : IDisposable
    {
        private static AsyncTextureLoader _Instance;

        /// <summary>
        /// Gets an instance of the loader.
        /// </summary>
        public static AsyncTextureLoader Instance
        {
            get
            {
                if (_Instance == null)
                {
                    _Instance = new AsyncTextureLoader();
                }

                return _Instance;
            }
        }

        public AsyncTextureLoader()
        {
            if (_Instance == null)
            {
                _Instance = this;
            }

            Application.quitting += Dispose;
        }

        private AsyncMonitor _asyncMonitor = new AsyncMonitor();
        private ComputeShader _computeShader;
        private ComputeBuffer _computeBuffer;

        /// <summary>
        /// Gets or sets the time budget in milliseconds.
        /// </summary>
        public float UploadTimeSlice { get; set; } = 3.0f;

        /// <summary>
        /// Gets or sets the buffer size when writing to the GPU.
        /// </summary>
        public int BufferSize { get; set; } = (int)Math.Pow(2, 15) * 4;


        /// <summary>
        /// Gets or set the initial compute buffer size (in pixel count)
        /// </summary>
        public int InitialComputeBufferSize { get; set; } = 4096 * 4096;

        private readonly int _widthProp = Shader.PropertyToID("Width");
        private readonly int _heightProp = Shader.PropertyToID("Height");
        private readonly int _imageHeightProp = Shader.PropertyToID("ImageHeight");
        private readonly int _offsetXProp = Shader.PropertyToID("OffsetX");
        private readonly int _offsetYProp = Shader.PropertyToID("OffsetY");
        private readonly int _resultProp = Shader.PropertyToID("Result");
        private readonly int _inputProp = Shader.PropertyToID("Input");

        private bool _disposed;

        private void Init()
        {
            Debug.Log("Loading compute shader...");
            _computeShader = Resources.Load<ComputeShader>("Shaders/TextureUpload");

#if ZOMG_DEBUG
            Debug.Log($"{nameof(SystemInfo.supportsComputeShaders)}: {SystemInfo.supportsComputeShaders}");
            Debug.Log($"{nameof(SystemInfo.maxComputeWorkGroupSize)}: {SystemInfo.maxComputeWorkGroupSize}");
            Debug.Log($"{nameof(SystemInfo.maxComputeWorkGroupSizeX)}: {SystemInfo.maxComputeWorkGroupSizeX}");
            Debug.Log($"{nameof(SystemInfo.maxComputeWorkGroupSizeY)}: {SystemInfo.maxComputeWorkGroupSizeY}");
            Debug.Log($"{nameof(SystemInfo.maxComputeWorkGroupSizeZ)}: {SystemInfo.maxComputeWorkGroupSizeZ}");
#endif
        }


        /// <summary>
        /// Pre-warms the compute shader with the default initial compute buffer size.
        /// </summary>
        public void Prewarm()
        {
            Prewarm(InitialComputeBufferSize, 1);
        }

        /// <summary>
        /// Pre-warms the compute shader for textures of the given resolution.
        /// </summary>
        /// <param name="width"></param>
        /// <param name="height"></param>
        public async void Prewarm(int width, int height)
        {
            if (_computeShader == null)
            {
                InitialComputeBufferSize = width * height;
                Init();
                CreateComputeBuffer(InitialComputeBufferSize);

                var rt = await AcquireTextureAsync(1, 1, 0, true);
                try
                {
                    await UploadDataAsync(rt, 1, 1, new byte[4]);
                }
                finally
                {
                    RenderTexture.ReleaseTemporary(rt);
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Debug.Log("Disposing async texture loader");
                _asyncMonitor?.Dispose();
                _computeBuffer?.Dispose();

                _disposed = true;

                Application.quitting -= Dispose;
            }
        }


        private async Task<ComputeBuffer> AcquireBuffer(int width, int height, CancellationToken token)
        {
#if ZOMG_DEBUG
            Debug.Log("Waiting for my turn...");
#endif

            await _asyncMonitor.WaitAsync(token);

#if ZOMG_DEBUG
            Debug.Log("It's my turn!");
#endif

            if (!_computeShader)
            {
                Init();
            }

            int requiredLength = width * height;
            if (_computeBuffer == null || _computeBuffer.count < requiredLength)
            {
                int size = Math.Max(ToPowerOfTwo(requiredLength), InitialComputeBufferSize);
                CreateComputeBuffer(size);
            }

            return _computeBuffer;
        }

        private void CreateComputeBuffer(int size)
        {
            Debug.Log($"Creating compute buffer of {size * 4 / 1000 / 1000}MiB");

            // Dispose old
            _computeBuffer?.Dispose();

            // Create new
            _computeBuffer = new ComputeBuffer(size, sizeof(uint), ComputeBufferType.Default, ComputeBufferMode.SubUpdates);
        }

        private void ReturnBuffer()
        {
#if ZOMG_DEBUG
                Debug.Log("Returning buffer...");
#endif
            // await Task.Yield();
            _asyncMonitor.Pulse();
        }

        private static int ToPowerOfTwo(int number)
        {
            return (int)Mathf.Pow(2, Mathf.CeilToInt(Mathf.Log(number, 2)));
        }

        #region Public API

        /// <summary>
        /// Decode image using <see cref="ImageDecoder"/>.
        /// </summary>
        /// <param name="input"></param>
        /// <param name="imageDecoder"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task<DecodedImage> DecodeImageAsync(byte[] input, IAsyncImageDecoder imageDecoder, CancellationToken cancellationToken = default)
        {
            return DecodeImageAsync(new MemoryStream(input), imageDecoder, cancellationToken);
        }

        /// <summary>
        /// Decode image
        /// </summary>
        /// <param name="input"></param>
        /// <param name="imageDecoder"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task<DecodedImage> DecodeImageAsync(Stream input, IAsyncImageDecoder imageDecoder, CancellationToken cancellationToken = default)
        {
            return imageDecoder.DecodeImageAsync(input, cancellationToken);
        }


        /// <summary>
        /// Acquires a render texture that is compatible with this class. Can be called from any thread.
        /// </summary>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <param name="mipCount"></param>
        /// <param name="temporary"></param>
        /// <returns></returns>
        public async Task<RenderTexture> AcquireTextureAsync(int width, int height, int mipCount = -1, bool temporary = false, CancellationToken token = default)
        {
            // Switch to main thread if need be and possible
            await MainThreadRegister.Context;
            
            token.ThrowIfCancellationRequested();

            var descriptor = new RenderTextureDescriptor(width, height,
                SystemInfo.GetCompatibleFormat(GraphicsFormat.R8G8B8A8_UNorm, FormatUsage.SetPixels), 0,
                mipCount)
            {
                enableRandomWrite = true,
                autoGenerateMips = false,
                useMipMap = mipCount != 0
            };

            var tex = temporary
                ? RenderTexture.GetTemporary(descriptor)
                : new RenderTexture(descriptor);

            tex.name = nameof(AsyncTextureLoader);
            if (!temporary)
            {
                tex.Create();
                token.Register(() =>
                {
                    if (tex && tex.name == nameof(AsyncTextureLoader))
                        UnityEngine.Object.DestroyImmediate(tex);
                });
            }
            else
            {
                token.Register(() => 
                {
                    if (tex && tex.name == nameof(AsyncTextureLoader)) 
                        tex.Release();
                });
            }
            return tex;
        }


        /// <summary>
        /// Asynchronously updates part of a texture with the provided pixel data.
        /// [IMPORTANT] For the time being, the data layout must be RGBA32.
        /// </summary>
        /// <param name="texture"></param>
        /// <param name="image"></param>
        /// <param name="token">An optional cancellation token which might trigger a <see cref="OperationCanceledException"/></param>
        /// <exception cref="OperationCanceledException">If the operation was cancelled.</exception>
        /// <exception cref="AssertionException">If the pre conditions weren't met.</exception>
        public async Task UploadDataAsync(RenderTexture texture, DecodedImage image, CancellationToken token = default)
        {
            await UploadDataAsync(texture, 0, 0, image.Width, image.Height, 0, image.Data, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Asynchronously updates part of a texture with the provided pixel data.
        /// [IMPORTANT] For the time being, the data layout must be RGBA32.
        /// </summary>
        /// <param name="texture"></param>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <param name="data"></param>
        /// <param name="token">An optional cancellation token which might trigger a <see cref="OperationCanceledException"/></param>
        /// <exception cref="OperationCanceledException">If the operation was cancelled.</exception>
        /// <exception cref="AssertionException">If the pre conditions weren't met.</exception>
        public async Task UploadDataAsync(RenderTexture texture, int width, int height, byte[] data, CancellationToken token = default)
        {
            await UploadDataAsync(texture, 0, 0, width, height, 0, data, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Asynchronously updates part of a texture with the provided pixel data.
        /// [IMPORTANT] For the time being, the data layout must be RGBA32.
        /// </summary>
        /// <param name="texture">The texture to copy the data into. Must have the <see cref="RenderTexture.enableRandomWrite"/> flag enabled.</param>
        /// <param name="xOffset">X offset from which to copy.</param>
        /// <param name="yOffset">Y offset from which to copy. Goes from top to bottom.</param>
        /// <param name="width">Amount in the x dimension to copy.</param>
        /// <param name="height">Amount in the y dimension to copy.</param>
        /// <param name="mipLevel">Which mip level to copy into. NOTE: Not properly implemented yet! Mips are automatically generated.</param>
        /// <param name="data">The actual pixel data as RGBA32.</param>
        /// <param name="token">An optional cancellation token which might trigger a <see cref="OperationCanceledException"/></param>
        /// <exception cref="OperationCanceledException">If the operation was canceled.</exception>
        /// <exception cref="AssertionException">If the pre conditions weren't met.</exception>
        public async Task UploadDataAsync(RenderTexture texture, int xOffset, int yOffset, int width, int height, int mipLevel, byte[] data,
            CancellationToken token = default)
        {
            await MainThreadRegister.Context;
            
            // Check for cancellation
            token.ThrowIfCancellationRequested();

            // Check pre-conditions 
            Assert.IsTrue(SystemInfo.supportsComputeShaders);
            Assert.IsTrue(texture.width >= xOffset + width);
            Assert.IsTrue(texture.height >= yOffset + height);
            Assert.IsTrue(xOffset >= 0);
            Assert.IsTrue(yOffset >= 0);

            var computeBuffer = await AcquireBuffer(width, height, token);
            try
            {
                // Upload to compute buffer (CPU -> GPU)
                int written = 0;
                var sw = Stopwatch.StartNew();

#if ZOMG_DEBUG
                Debug.Log("Starting copying...");
#endif

                while (written < data.Length)
                {
                    // Check for cancellation
                    token.ThrowIfCancellationRequested();

                    int toWrite = Mathf.Min(data.Length - written, BufferSize);
                    var pixelBuffer = computeBuffer.BeginWrite<uint>(written / 4, toWrite / 4);
                    var buffer = pixelBuffer.Reinterpret<byte>(sizeof(uint));

                    NativeArray<byte>.Copy(data, written, buffer, 0, toWrite);

                    written += toWrite;
                    computeBuffer.EndWrite<uint>(toWrite / 4);

                    if (sw.Elapsed.TotalMilliseconds > UploadTimeSlice)
                    {
#if ZOMG_DEBUG
                Debug.Log("Pausing...");
#endif
                        await Task.Yield();
                        sw.Restart();
                    }
                }

                await Task.Yield();

#if ZOMG_DEBUG
                Debug.Log("Done copying!");
#endif

                // Check for cancellation
                token.ThrowIfCancellationRequested();

                // Copy to texture (GPU -> GPU)
                _computeShader.SetTexture(0, _resultProp, texture, mipLevel);
                _computeShader.SetInt(_widthProp, width);
                _computeShader.SetInt(_heightProp, height);
                _computeShader.SetInt(_offsetXProp, xOffset);
                _computeShader.SetInt(_offsetYProp, yOffset);
                _computeShader.SetInt(_imageHeightProp, texture.height);
                _computeShader.SetBuffer(0, _inputProp, computeBuffer);

                _computeShader.Dispatch(0, Mathf.CeilToInt(width / 8.0f), Mathf.CeilToInt(height / 8.0f), 1);


#if ZOMG_DEBUG
                Debug.Log("Compute shader dispatched!");
#endif
                // Wait a frame
                await Task.Yield();
                
                token.ThrowIfCancellationRequested();

                if (texture.useMipMap)
                {
#if ZOMG_DEBUG
                Debug.Log("Generating mips...");
#endif
                    texture.GenerateMips();
                }

                // Check for cancellation
                token.ThrowIfCancellationRequested();
            }
            finally
            {
                // Return buffer for others
                ReturnBuffer();
            }
        }


        /// <summary>
        /// Asynchronously loads a texture. Will automatically fall back to the blocking approach if compute shaders are not supported.
        /// </summary>
        /// <param name="input"></param>
        /// <param name="mipCount"></param>
        /// <returns></returns>
        public async Task<Texture> LoadTextureAsync(Stream input, IAsyncImageDecoder imageDecoder, int mipCount = -1, CancellationToken token = default)
        {
            if (SystemInfo.supportsComputeShaders)
            {
                var image = await DecodeImageAsync(input, imageDecoder, token);
                token.ThrowIfCancellationRequested();
                var texture = await AcquireTextureAsync(image.Width, image.Height, mipCount, false, token);
                await UploadDataAsync(texture, 0, 0, image.Width, image.Height, 0, image.Data, token);
                return texture;
            }
            else
            {
                Debug.LogWarning("System does not support compute shaders -- falling back to built-in method.");
                var texture = new Texture2D(1, 1);
                var bytes = new MemoryStream();
                await input.CopyToAsync(bytes);
                texture.LoadImage(bytes.ToArray());
                return texture;
            }
        }

        /// <summary>
        /// Asynchronously loads a texture. Will automatically fall back to the blocking approach if compute shaders are not supported.
        /// </summary>
        /// <param name="bytes"></param>
        /// <param name="mipCount"></param>
        /// <returns></returns>
        public Task<Texture> LoadTextureAsync(byte[] bytes, IAsyncImageDecoder imageDecoder, int mipCount = -1)
        {
            return LoadTextureAsync(new MemoryStream(bytes), imageDecoder, mipCount);
        }

        #endregion
    }
}