using System;
using System.Buffers;
using System.Collections;                        // ← корутины
using System.Threading.Channels;
using System.Threading.Tasks;
using UnityEngine;
using Live2D.CubismMotionSyncPlugin.Framework; // ← нужный namespace для CubismMotionSyncController
using Live2D.Cubism.Core;                       // ← для CubismModel
using Live2D.Cubism.Rendering;                  // ← для CubismRenderer
using Live2D.Cubism.Framework.Raycasting;       // ← для raycast

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
    [SerializeField] float eosSilenceSec = 0.7f;

    /* --------‑‑‑ Анимация ----------------------------------------------- */
    [Header("Animator")]
    public Animator animator;                                        // ①

    [Header("Live2D Motion Sync")]
    public CubismMotionSyncController motionSync;                    // ②

    [Header("Live2D Model")]
    public CubismModel cubismModel;                                  // ③
    
    [Header("Live2D Clickable Renderer")]
    public CubismRenderer clickableRenderer;                         // ③‑а

    [Header("Sleep Audio")]
    public AudioClip sleepAudioClip;                                 // ⑥

    /* --------‑‑‑ внутреннее состояние ----------------------------------- */
    CubismRaycaster raycaster;                                       // ④
    CubismRaycastHit[] raycastResults;
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

        // Инициализация raycast системы
        InitializeRaycast();                                        // ④‑а

        // Запускаем проверку готовности модели
        StartCoroutine(CheckModelReady());                          // ⑤
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
        if (string.IsNullOrEmpty(stateName)) return;

        // Специальная обработка для Sleep
        if (stateName.Equals("Sleep", System.StringComparison.OrdinalIgnoreCase))
        {
            StartCoroutine(SleepEmotionSequence());
            return;
        }

        // Обычная обработка для других анимаций
        if (animator)
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
    void Update()
    {
        // Обнаружение кликов по Live2D модели                     // ④‑б
        if (Input.GetMouseButtonDown(0))
        {
            HandleClick();
        }
    }

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
        {
            PlayAnimationState("Idle");                            // первое переключение
            yield return new WaitForSeconds(0.1f);                 // небольшая пауза между переключениями
            if (!playing)                                           // проверяем еще раз
                PlayAnimationState("Idle");                        // второе переключение
        }
    }

    /* --------‑‑‑ Корутина для последовательности Sleep ------------------- */
    IEnumerator SleepEmotionSequence()                               // ⑥‑б
    {
        // Сохраняем оригинальный клип и настройки
        var originalClip = src.clip;
        var originalLoop = src.loop;
        
        // 1. Запускаем анимацию Dejected
        if (animator) animator.Play("Dejected", 0, 0f);
        
        // 2. Ждем 0.5 секунды
        yield return new WaitForSeconds(0.5f);
        
        // 3. Запускаем аудио клип и включаем motion sync
        if (sleepAudioClip != null && src != null)
        {
            src.Stop(); // останавливаем текущий звук если играет
            src.clip = sleepAudioClip;
            src.loop = false; // фиксированный клип не должен зацикливаться
            src.Play();
            
            // Включаем motion sync на время проигрывания клипа
            if (motionSync) motionSync.enabled = true;
        }
        
        // 4. Ждем 7.2 секунды (общее время с начала)
        yield return new WaitForSeconds(6.7f); // 7.2 - 0.5 = 6.7
        
        // 5. Восстанавливаем оригинальные настройки аудио
        src.Stop();
        src.clip = originalClip;
        src.loop = originalLoop;
        
        // 6. Выключаем motion sync и включаем анимацию Sleep
        if (motionSync) motionSync.enabled = false;
        if (animator) animator.Play("Sleep", 0, 0f);
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

    /* --------‑‑‑ Raycast методы ----------------------------------------- */
    void InitializeRaycast()                                         // ④‑в
    {
        if (cubismModel == null)
        {
            Debug.LogWarning("CubismModel не назначен в StreamingTTSPlayer. Raycast не будет работать.");
            return;
        }

        if (clickableRenderer == null)
        {
            Debug.LogWarning("ClickableRenderer не назначен в StreamingTTSPlayer. Raycast не будет работать.");
            return;
        }

        // Получаем или добавляем CubismRaycaster к объекту с CubismModel
        raycaster = cubismModel.GetComponent<CubismRaycaster>();
        if (raycaster == null)
        {
            raycaster = cubismModel.gameObject.AddComponent<CubismRaycaster>();
        }

        // Инициализируем буфер для результатов raycast
        raycastResults = new CubismRaycastHit[4];

        // Добавляем CubismRaycastable только к указанному рендереру
        var raycastable = clickableRenderer.GetComponent<CubismRaycastable>();
        if (raycastable == null)
        {
            raycastable = clickableRenderer.gameObject.AddComponent<CubismRaycastable>();
            raycastable.Precision = CubismRaycastablePrecision.BoundingBox;
        }

        Debug.Log($"Raycast инициализирован для рендерера: {clickableRenderer.name}");
    }

    void HandleClick()                                               // ④‑г
    {
        if (raycaster == null) return;

        // Создаем луч из позиции мыши
        var ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        var hitCount = raycaster.Raycast(ray, raycastResults);

        // Проверяем попадания
        if (hitCount > 0)
        {
            Debug.Log("heart_clicked - Live2D модель была кликнута!");
            SendToFlutter.Send("heart_clicked");
        }
    }

    /* --------‑‑‑ Проверка готовности модели ----------------------------- */
    IEnumerator CheckModelReady()                                    // ⑤‑а
    {
        // Простое ожидание - несколько кадров после инициализации
        yield return new WaitForEndOfFrame();
        yield return new WaitForEndOfFrame(); 
        yield return new WaitForEndOfFrame();

        Debug.Log("Live2D модель готова!");
        SendToFlutter.Send("model_loaded");
    }
}