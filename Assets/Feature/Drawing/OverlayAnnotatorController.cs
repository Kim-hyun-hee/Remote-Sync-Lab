using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// 입력(마우스)로 드로잉을 생성하고, NetBridge로 네트워크 전송.
/// 
/// - 로컬 즉시 렌더: UX를 위해 내 화면에서 바로 보이게
/// - 네트워크 렌더: Hub가 RPC로 받아서 (authorId, strokeId) 키로 다시 그림
/// 
/// 주의:
/// - 로컬 즉시 렌더는 authorId=-1 같은 임시 키를 사용.
/// - 네트워크에서 다시 그려지는 스트로크는 실제 authorId 키를 사용.
/// - 그래서 내 화면에서는 "로컬 선"과 "네트워크 선"이 겹쳐 보일 수 있다.
///   (이걸 싫어하면 InvokeLocal=true로 바꾸거나, 로컬 즉시 렌더를 제거하는 정책을 선택하면 됨)
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class OverlayAnnotatorControllerOptimized : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private UIToolkitOverlayAnnotatorOptimized annotator;
    [SerializeField] private AnnotationNetBridgeOptimized netBridge;

    [Header("Brush")]
    [SerializeField] private float widthPx = 4f;

    private UIDocument _doc;
    private VisualElement _root;

    private bool _dragging;
    private OverlayStrokeKey _localKey;
    private int _localStrokeId;

    private Color32 _currentColor = new Color32(255, 0, 0, 255);

    private void Awake()
    {
        _doc = GetComponent<UIDocument>();
    }

    private void Start()
    {
        _root = _doc.rootVisualElement;

        if (annotator == null) annotator = GetComponent<UIToolkitOverlayAnnotatorOptimized>();
        if (netBridge == null) netBridge = GetComponent<AnnotationNetBridgeOptimized>();

        BuildPaletteUI();
    }

    private void Update()
    {
        if (annotator == null || netBridge == null) return;
        if (!annotator.IsReady) return;

        // 단축키로 색 변경
        if (Input.GetKeyDown(KeyCode.Alpha1)) _currentColor = new Color32(255, 0, 0, 255);
        if (Input.GetKeyDown(KeyCode.Alpha2)) _currentColor = new Color32(0, 255, 0, 255);
        if (Input.GetKeyDown(KeyCode.Alpha3)) _currentColor = new Color32(0, 128, 255, 255);
        if (Input.GetKeyDown(KeyCode.Alpha4)) _currentColor = new Color32(255, 255, 0, 255);
        if (Input.GetKeyDown(KeyCode.Alpha5)) _currentColor = new Color32(255, 255, 255, 255);

        if (Input.GetMouseButtonDown(0))
            TryBeginStroke(Input.mousePosition);

        if (_dragging && Input.GetMouseButton(0))
            TryAddPoint(Input.mousePosition);

        if (_dragging && Input.GetMouseButtonUp(0))
            EndStroke();

        // C: 전체 클리어(테스트)
        if (Input.GetKeyDown(KeyCode.C))
            netBridge.NetClearAll();
    }

    private void TryBeginStroke(Vector2 screenPos)
    {
        if (!annotator.TryScreenToNormalized(screenPos, out var firstNorm))
            return;

        _localStrokeId = netBridge.NetBeginStroke(_currentColor, widthPx, firstNorm);
        if (_localStrokeId < 0) return;

        // 로컬 즉시 렌더용 키
        //_localKey = new OverlayStrokeKey(authorId: -1, strokeId: _localStrokeId);

        //annotator.BeginStroke(_localKey, _currentColor, widthPx);
        //annotator.AddPoints(_localKey, new[] { firstNorm });

        _dragging = true;
    }

    private void TryAddPoint(Vector2 screenPos)
    {
        if (!annotator.TryScreenToNormalized(screenPos, out var norm))
            return;

        netBridge.NetAddPoint(norm);

        // 로컬 즉시 렌더
        //annotator.AddPoints(_localKey, new[] { norm });
    }

    private void EndStroke()
    {
        netBridge.NetEndStroke();
        //annotator.EndStroke(_localKey);
        _dragging = false;
    }

    private void BuildPaletteUI()
    {
        // 상단 간단 툴바
        var bar = new VisualElement();
        bar.style.position = Position.Absolute;
        bar.style.left = 8;
        bar.style.top = 8;
        bar.style.flexDirection = FlexDirection.Row;
        bar.style.marginLeft = 6;
        bar.style.marginRight = 6;
        bar.style.marginTop = 6;
        bar.style.marginBottom = 6;
        bar.style.paddingLeft = 8;
        bar.style.paddingRight = 8;
        bar.style.paddingTop = 6;
        bar.style.paddingBottom = 6;
        bar.style.backgroundColor = new Color(0, 0, 0, 0.35f);
        bar.style.borderBottomLeftRadius = 8;
        bar.style.borderBottomRightRadius = 8;
        bar.style.borderTopLeftRadius = 8;
        bar.style.borderTopRightRadius = 8;

        _root.Add(bar);

        void AddColorButton(string name, Color32 c)
        {
            var btn = new Button(() => _currentColor = c) { text = name };
            btn.style.minWidth = 46;
            bar.Add(btn);
        }

        AddColorButton("R", new Color32(255, 0, 0, 255));
        AddColorButton("G", new Color32(0, 255, 0, 255));
        AddColorButton("B", new Color32(0, 128, 255, 255));
        AddColorButton("Y", new Color32(255, 255, 0, 255));
        AddColorButton("W", new Color32(255, 255, 255, 255));

        var clearBtn = new Button(() => netBridge.NetClearAll()) { text = "Clear" };
        bar.Add(clearBtn);
    }
}
