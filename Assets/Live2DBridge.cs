using System;
using System.Buffers;
using System.Collections;                        // ← корутины
using System.Threading.Channels;
using System.Threading.Tasks;
using UnityEngine;
using Live2D.CubismMotionSyncPlugin.Framework; // ← нужный namespace для CubismMotionSyncController

public sealed class StreamingTTSPlayer : MonoBehaviour
{
    /* --------‑‑‑ Audio‑настройки ---------------------------------------- */
    const int SampleRate = 24_000;
    const int Channels   = 1;

    [Header("Prefill, ms")]
    [SerializeField] int  prefillMs = 0;

    [Header("Ring length, sec")]
    [SerializeField] int  ringLengthSec = 30;

    [Header("Silence‑timeout, sec")]
    [Tooltip("Сколько секунд подряд буфер должен быть пуст, "
             + "чтобы считать поток оконченным")]
    [SerializeField] float eosSilenceSec = 0.25f;

    /* --------‑‑‑ Анимация ----------------------------------------------- */
    [Header("Animator")]
    public Animator animator;                                        // ①

    [Header("Live2D Motion Sync")]
    public CubismMotionSyncController motionSync;                    // ②

    /* --------‑‑‑ внутреннее состояние ----------------------------------- */
    float[] ring;
    int     ringSize, readPos, writePos;
    readonly object gate = new object();

    Channel<string> inChannel = Channel.CreateUnbounded<string>();
    static readonly ArrayPool<byte> BytePool = ArrayPool<byte>.Shared;

    public AudioSource src;
    AudioClip   clip;

    bool  playing;
    float silenceTimer;

    /* --------‑‑‑ Lifecycle ---------------------------------------------- */
    void Awake()
    {
        InitClip();
        _ = Task.Run(DecodeLoop);

        if (motionSync) motionSync.enabled = false;                 // ②‑а
    }

    void OnDestroy() => inChannel.Writer.TryComplete();

    /* --------‑‑‑ Публичные вызовы из Flutter ----------------------------- */
    public void OnAudioChunk(string b64)
    {
        // пустая строка => явный маркер EOS; держим для совместимости
        if (string.IsNullOrEmpty(b64))
        {
            inChannel.Writer.Complete();
            return;
        }
        inChannel.Writer.TryWrite(b64);
    }

    /// <summary>Вызов анимационного стейта по имени (доступно из Flutter).</summary>
    public void PlayAnimationState(string stateName)                // ①‑а
    {
        if (animator && !string.IsNullOrEmpty(stateName))
            animator.Play(stateName, 0, 0f);
    }

    /* --------‑‑‑ Фоновое декодирование ----------------------------------- */
    async Task DecodeLoop()
    {
        await foreach (var b64 in inChannel.Reader.ReadAllAsync())
        {
            int expected = (b64.Length >> 2) * 3;
            var buf = BytePool.Rent(expected);

            if (Convert.TryFromBase64String(b64, buf, out int written))
            {
                var block = new float[written >> 2];
                Buffer.BlockCopy(buf, 0, block, 0, written);
                WriteToRing(block);
            }
            BytePool.Return(buf);
        }
    }

    /* --------‑‑‑ Update‑цикл -------------------------------------------- */
    void LateUpdate()
    {
        /* 1) старт плеера, когда предзаполнен буфер */
        // Начинаем только если реально пришли данные
        if (!playing && SamplesAvailable() > 0 &&
            SamplesAvailable() >= PrefillSamples())
        {
            src.timeSamples = readPos;
            src.Play();
            playing = true;

            if (motionSync) motionSync.enabled = true;              // ②‑b
        }

        /* 2) счётчик тишины */
        if (playing && SamplesAvailable() == 0)
            silenceTimer += Time.deltaTime;
        else
            silenceTimer = 0f;

        /* 3) условие завершения */
        if (playing && silenceTimer >= eosSilenceSec)
        {
            if (src.isPlaying) src.Stop();
            SendToFlutter.Send("AudioEnded");

            if (motionSync) motionSync.enabled = false;             // ②‑c

            StartCoroutine(PlayIdleAfterDelay());                   // ③

            playing = false;
            silenceTimer = 0f;

            InitClip();
            inChannel = Channel.CreateUnbounded<string>();
            _ = Task.Run(DecodeLoop);
        }
    }

    /* --------‑‑‑ Корутина для стейта Idle ------------------------------- */
    IEnumerator PlayIdleAfterDelay()                                 // ③‑а
    {
        yield return new WaitForSeconds(0.5f);
        if (!playing)                                               // если уже не началось новое воспроизведение
            PlayAnimationState("Idle");
    }

    /* --------‑‑‑ PCM Reader --------------------------------------------- */
    void PCMReader(float[] data)
    {
        int need = data.Length;
        lock (gate)
        {
            int avail = SamplesAvailable();
            int take  = Mathf.Min(need, avail);
            int tail  = ringSize - readPos;

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

    /* --------‑‑‑ Служебные методы --------------------------------------- */
    int PrefillSamples() => prefillMs * SampleRate / 1_000;

    void InitClip()
    {
        ringSize = ringLengthSec * SampleRate;
        ring     = new float[ringSize];
        readPos  = writePos = 0;

        clip?.UnloadAudioData();
        clip = AudioClip.Create(nameof(StreamingTTSPlayer),
                                ringSize, Channels, SampleRate,
                                true, PCMReader);

        src.clip        = clip;
        src.loop        = true;
        src.playOnAwake = false;
    }

    void WriteToRing(float[] srcData)
    {
        int len = srcData.Length;
        lock (gate)
        {
            int free = ringSize - SamplesAvailable() - 1;
            len = Mathf.Min(len, free);

            int tail = ringSize - writePos;
            if (len <= tail)
                Array.Copy(srcData, 0, ring, writePos, len);
            else
            {
                Array.Copy(srcData, 0, ring, writePos, tail);
                Array.Copy(srcData, tail, ring, 0, len - tail);
            }
            writePos = (writePos + len) % ringSize;
        }
    }

    int SamplesAvailable() => (writePos - readPos + ringSize) % ringSize;
}
