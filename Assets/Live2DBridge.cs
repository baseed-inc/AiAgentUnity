using System;
using UnityEngine;

public class StreamingTTSPlayer : MonoBehaviour
{
    const int SampleRate = 24000;
    const int Channels = 1;

    [Header("Latency (ms)")]
    [SerializeField] int prefillMs = 200;
    [SerializeField] int lowWaterMs = 100;

    [Header("Ring‑buffer length (sec)")]
    [SerializeField] int ringLengthSec = 30;

    float[] ring;
    int ringSize;
    int readPos;
    int writePos;
    readonly object gate = new object();

    AudioClip clip;
    public AudioSource src;

    bool streamActive;
    bool streamEnded;

    void Awake()
    {
        InitNewClip();
    }

    void InitNewClip()
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
        if (string.IsNullOrEmpty(b64)) return;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); } catch { return; }
        int count = bytes.Length >> 2;
        if (count == 0) return;

        float[] tmp = new float[count];
        Buffer.BlockCopy(bytes, 0, tmp, 0, bytes.Length);

        lock (gate)
        {
            int free = (readPos + ringSize - writePos - 1) % ringSize;
            if (count > free) count = free;
            int tail = ringSize - writePos;
            if (count <= tail) Array.Copy(tmp, 0, ring, writePos, count);
            else
            {
                Array.Copy(tmp, 0, ring, writePos, tail);
                Array.Copy(tmp, tail, ring, 0, count - tail);
            }
            writePos = (writePos + count) % ringSize;
        }

        if (!streamActive && MillisAvailable() >= prefillMs)
        {
            src.timeSamples = readPos;
            src.Play();
            streamActive = true;
        }
    }

    public void EndOfStream() => streamEnded = true;

    void PCMReader(float[] data)
    {
        int need = data.Length;
        lock (gate)
        {
            int avail = (writePos - readPos + ringSize) % ringSize;
            int take = need <= avail ? need : avail;
            int tail = ringSize - readPos;
            if (take <= tail) Array.Copy(ring, readPos, data, 0, take);
            else
            {
                Array.Copy(ring, readPos, data, 0, tail);
                Array.Copy(ring, 0, data, tail, take - tail);
            }
            readPos = (readPos + take) % ringSize;
            if (take < need) Array.Clear(data, take, need - take);
        }
    }

    void Update()
    {
        if (!streamActive) return;

        if (src.isPlaying)
        {
            if (MillisAvailable() < lowWaterMs) src.Pause();
        }
        else
        {
            if (MillisAvailable() >= prefillMs) src.UnPause();
        }

        if (streamEnded && MillisAvailable() == 0 && !src.isPlaying)
        {
            src.Stop();
            streamActive = false;
            streamEnded = false;
            InitNewClip();
            SendToFlutter.Send("AudioEnded");
        }
    }

    int MillisAvailable()
    {
        int samples = (writePos - readPos + ringSize) % ringSize;
        return samples * 1000 / SampleRate;
    }
}
