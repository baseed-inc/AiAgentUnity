using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEngine;

public class StreamingTTSPlayer : MonoBehaviour
{
    const int SampleRate = 24000;
    const int Channels = 1;
    [Header("Startup Latency, ms")]
    [SerializeField] int prefillMs = 0;
    [Header("Ring length, sec")]
    [SerializeField] int ringLengthSec = 30;
    float[] ring;
    int ringSize;
    int readPos;
    int writePos;
    readonly object gate = new object();
    readonly BlockingCollection<string> incoming = new BlockingCollection<string>();
    readonly ConcurrentQueue<float[]> decoded = new ConcurrentQueue<float[]>();
    AudioClip clip;
    public AudioSource src;
    bool playing;
    bool draining;
    static readonly ArrayPool<byte> bytePool = ArrayPool<byte>.Shared;

    void Awake()
    {
        InitClip();
        Task.Run(DecodeLoop);
    }

    void InitClip()
    {
        ringSize = ringLengthSec * SampleRate;
        ring = new float[ringSize];
        readPos = writePos = 0;
        clip = AudioClip.Create("StreamingTTS", ringSize, Channels, SampleRate, true, PCMReader);
        src.clip = clip;
        src.loop = true;
        src.playOnAwake = false;
    }

    public void OnAudioChunk(string b64)
    {
        if (!string.IsNullOrEmpty(b64)) incoming.Add(b64);
    }

    void DecodeLoop()
    {
        foreach (var b64 in incoming.GetConsumingEnumerable())
        {
            int expected = (b64.Length >> 2) * 3;
            byte[] bytes = bytePool.Rent(expected);
            if (!Convert.TryFromBase64String(b64, bytes, out int written))
            {
                bytePool.Return(bytes);
                continue;
            }
            int sampleCount = written >> 2;
            float[] block = new float[sampleCount];
            Buffer.BlockCopy(bytes, 0, block, 0, written);
            bytePool.Return(bytes);
            decoded.Enqueue(block);
        }
    }

    public void EndOfStream()
    {
        draining = true;
    }

    void LateUpdate()
    {
        while (decoded.TryDequeue(out var block)) WriteToRing(block);
        if (!playing && MillisAvailable() >= prefillMs)
        {
            src.timeSamples = readPos;
            src.Play();
            playing = true;
        }
        if (draining && MillisAvailable() == 0 && !src.isPlaying)
        {
            src.Stop();
            playing = false;
            draining = false;
            InitClip();
            SendToFlutter.Send("AudioEnded");
        }
    }

    void PCMReader(float[] data)
    {
        int need = data.Length;
        lock (gate)
        {
            int avail = (writePos - readPos + ringSize) % ringSize;
            int take = Mathf.Min(need, avail);
            int tail = ringSize - readPos;
            if (take <= tail)
                Array.Copy(ring, readPos, data, 0, take);
            else
            {
                Array.Copy(ring, readPos, data, 0, tail);
                Array.Copy(ring, 0, data, tail, take - tail);
            }
            readPos = (readPos + take) % ringSize;
            if (take < need) Array.Clear(data, take, need - take);
        }
    }

    void WriteToRing(float[] src)
    {
        int len = src.Length;
        lock (gate)
        {
            int free = (readPos + ringSize - writePos - 1) % ringSize;
            len = Mathf.Min(len, free);
            int tail = ringSize - writePos;
            if (len <= tail)
                Array.Copy(src, 0, ring, writePos, len);
            else
            {
                Array.Copy(src, 0, ring, writePos, tail);
                Array.Copy(src, tail, ring, 0, len - tail);
            }
            writePos = (writePos + len) % ringSize;
        }
    }

    int MillisAvailable()
    {
        int samples = (writePos - readPos + ringSize) % ringSize;
        return samples * 1000 / SampleRate;
    }

    void OnDestroy()
    {
        incoming.CompleteAdding();
    }
}
