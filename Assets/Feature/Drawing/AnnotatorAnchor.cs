using UnityEngine;

/// <summary>
/// "씬에서 어떤 Annotator 구현체를 사용할지"를 고정적으로 제공하는 앵커(Anchor).
///
/// 왜 필요하나?
/// - 네트워크 허브(AnnotationHub)는 "그리기 구현체"에 직접 의존하면 안 좋다.
///   (uGUI 기반(Texture2D)인지, UI Toolkit 기반(VectorDrawingElement)인지 바뀔 수 있음)
/// - 하지만 런타임에 "현재 씬에서 쓰는 Annotator가 무엇인지"는 알아야 한다.
///
/// 그래서 씬에 AnnotatorAnchor를 하나 두고:
/// - inspector에서 annotatorBehaviour를 연결해두면
/// - Hub가 Spawn될 때 Anchor.Instance.Annotator를 통해 구현체를 얻는다.
///
/// 장점:
/// - Hub 코드가 씬 구조 변경에 덜 민감해진다.
/// - 테스트 시, annotatorBehaviour만 교체하면 동일 Hub를 재사용 가능.
/// </summary>
public sealed class AnnotatorAnchor : MonoBehaviour
{
    /// <summary>
    /// 간단한 전역 접근용 싱글톤.
    ///
    /// 주의:
    /// - "진짜 싱글톤 패턴"이라기보다는,
    ///   씬에 1개만 둔다는 전제 하에 편의 접근을 제공하는 형태.
    /// </summary>
    public static AnnotatorAnchor Instance { get; private set; }

    /// <summary>
    /// 실제 annotator를 담는 MonoBehaviour 슬롯.
    ///
    /// 왜 MonoBehaviour로 받나?
    /// - Unity Inspector는 interface(IOverlayAnnotator) 타입을 직접 할당 못 한다.
    /// - 대신 MonoBehaviour로 받고, 런타임에 as IOverlayAnnotator로 캐스팅한다.
    ///
    /// 여기에 들어갈 수 있는 예:
    /// - Texture2DOverlayAnnotator
    /// - UIToolkitOverlayAnnotator
    /// </summary>
    [SerializeField] private MonoBehaviour annotatorBehaviour;

    /// <summary>
    /// 외부에는 IOverlayAnnotator로 노출한다.
    /// - 캐스팅 실패(= 잘못된 컴포넌트 넣음) 시 null일 수 있다.
    /// </summary>
    public IOverlayAnnotator Annotator => annotatorBehaviour as IOverlayAnnotator;

    private void Awake()
    {
        // 씬에 여러 개가 생기면 마지막 Awake가 덮어쓴다.
        // 실무에선 중복 배치를 방지하는 체크를 넣기도 한다.
        Instance = this;
    }

    private void OnDestroy()
    {
        // 자기 자신이 등록된 인스턴스였을 때만 null로 정리.
        if (Instance == this) Instance = null;
    }
}
