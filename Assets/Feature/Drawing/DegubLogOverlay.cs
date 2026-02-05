using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Unity Console 로그(Debug.Log/Warning/Error)를 가로채서
/// 화면 왼쪽 아래에 OnGUI로 표시하는 디버그 오버레이.
/// </summary>
public sealed class DebugLogOverlay : MonoBehaviour
{
    [Header("Overlay")]
    [Tooltip("화면에 표시할 최대 로그 줄 수")]
    [SerializeField] private int maxLines = 30;

    [Tooltip("한 줄 최대 표시 길이(너무 긴 스택트레이스 잘림 방지)")]
    [SerializeField] private int maxCharsPerLine = 220;

    [Tooltip("오버레이 패딩(화면 가장자리 여백)")]
    [SerializeField] private Vector2 padding = new Vector2(12, 12);

    [Tooltip("오버레이 영역 크기(폭/높이). 0이면 자동 계산")]
    [SerializeField] private Vector2 fixedSize = Vector2.zero;

    [Tooltip("배경 투명도(0~1). 0이면 배경 없음")]
    [Range(0f, 1f)]
    [SerializeField] private float backgroundAlpha = 0.35f;

    [Header("Filter")]
    [Tooltip("키워드가 비어있으면 전체 표시. 키워드가 있으면 해당 문자열 포함 로그만 표시")]
    [SerializeField] private string containsFilter = "";

    [Tooltip("Warning 로그 표시 여부")]
    [SerializeField] private bool showWarning = true;

    [Tooltip("Error/Exception 로그 표시 여부")]
    [SerializeField] private bool showError = true;

    [Tooltip("Log(일반) 로그 표시 여부")]
    [SerializeField] private bool showLog = true;

    [Header("Controls")]
    [Tooltip("F1로 오버레이 표시/숨김 토글")]
    [SerializeField] private bool allowToggleKey = true;

    [Tooltip("F2로 로그 클리어")]
    [SerializeField] private bool allowClearKey = true;

    [Tooltip("오버레이 기본 표시 여부")]
    [SerializeField] private bool visible = true;

    private readonly object _lock = new object();

    // 스레드 콜백에서 바로 List를 건드리면 위험하므로 큐에 적재
    private readonly Queue<LogItem> _pending = new Queue<LogItem>(256);

    // 실제 표시용 버퍼
    private readonly LinkedList<string> _lines = new LinkedList<string>();

    // IMGUI 스타일/리소스 (OnGUI에서만 안전하게 준비)
    private GUIStyle _style;
    private Texture2D _bgTex;

    private struct LogItem
    {
        public string condition;
        public string stackTrace;
        public LogType type;
    }

    private void Awake()
    {
        // Awake에서는 GUI 관련 접근을 하지 않는다.
        // (GUI.skin 등은 반드시 OnGUI 안에서만 안전)
        DontDestroyOnLoad(gameObject);

        // 배경 텍스처는 GUI 호출이 아니라 Texture 생성이라 Awake에서도 안전
        if (backgroundAlpha > 0f)
        {
            _bgTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _bgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, backgroundAlpha));
            _bgTex.Apply();
        }
    }

    private void OnEnable()
    {
        Application.logMessageReceivedThreaded += OnLogThreaded;
    }

    private void OnDisable()
    {
        Application.logMessageReceivedThreaded -= OnLogThreaded;
    }

    private void Update()
    {
        if (allowToggleKey && Input.GetKeyDown(KeyCode.F1))
            visible = !visible;

        if (allowClearKey && Input.GetKeyDown(KeyCode.F2))
            Clear();

        DrainPending();
    }

    /// <summary>
    /// 다른 스레드에서도 호출될 수 있으므로 큐에만 담는다.
    /// </summary>
    private void OnLogThreaded(string condition, string stackTrace, LogType type)
    {
        lock (_lock)
        {
            _pending.Enqueue(new LogItem
            {
                condition = condition,
                stackTrace = stackTrace,
                type = type
            });
        }
    }

    /// <summary>
    /// 메인 스레드에서 큐를 비워 화면 표시 버퍼로 옮긴다.
    /// </summary>
    private void DrainPending()
    {
        while (true)
        {
            LogItem item;

            lock (_lock)
            {
                if (_pending.Count == 0) break;
                item = _pending.Dequeue();
            }

            if (!PassTypeFilter(item.type)) continue;

            if (!string.IsNullOrEmpty(containsFilter))
            {
                if (item.condition == null ||
                    item.condition.IndexOf(containsFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
            }

            string line = FormatLine(item);

            if (line.Length > maxCharsPerLine)
                line = line.Substring(0, maxCharsPerLine) + "...";

            _lines.AddLast(line);

            while (_lines.Count > maxLines)
                _lines.RemoveFirst();
        }
    }

    private bool PassTypeFilter(LogType type)
    {
        switch (type)
        {
            case LogType.Error:
            case LogType.Exception:
            case LogType.Assert:
                return showError;

            case LogType.Warning:
                return showWarning;

            default:
                return showLog;
        }
    }

    private string FormatLine(LogItem item)
    {
        string prefix = item.type switch
        {
            LogType.Warning => "[W] ",
            LogType.Error => "[E] ",
            LogType.Exception => "[EX] ",
            LogType.Assert => "[A] ",
            _ => "[I] "
        };

        return prefix + (item.condition ?? string.Empty);
    }

    public void Clear()
    {
        _lines.Clear();
        lock (_lock) _pending.Clear();
    }

    private void EnsureGuiStyle()
    {
        // GUI.skin 접근은 OnGUI 안에서만 안전하므로 여기서만 생성
        if (_style != null) return;

        _style = new GUIStyle(GUI.skin.label)
        {
            fontSize = 15,
            wordWrap = true,
            richText = false
        };
    }

    private void OnGUI()
    {
        if (!visible) return;

        EnsureGuiStyle();

        float width = fixedSize.x > 0 ? fixedSize.x : Mathf.Min(900f, Screen.width * 0.55f);
        float height = fixedSize.y > 0 ? fixedSize.y : Mathf.Min(420f, Screen.height * 0.35f);

        float x = padding.x;
        float y = Screen.height - height - padding.y; // 왼쪽 아래

        var rect = new Rect(x, y, width, height);

        if (_bgTex != null)
            GUI.DrawTexture(rect, _bgTex, ScaleMode.StretchToFill);

        var sb = new StringBuilder(2048);
        foreach (var line in _lines)
            sb.AppendLine(line);

        var inner = new Rect(rect.x + 8, rect.y + 6, rect.width - 16, rect.height - 12);
        GUI.Label(inner, sb.ToString(), _style);

        var hint = new Rect(rect.x, rect.y - 18, rect.width, 18);
        GUI.Label(hint, "F1: Overlay Toggle   F2: Clear", _style);
    }
}
