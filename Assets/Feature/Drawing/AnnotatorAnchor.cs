using UnityEngine;

/// <summary>
/// Hub가 "렌더러(IOverlayAnnotator)"를 찾기 위한 앵커.
/// 
/// 왜 인스펙터로 연결하나?
/// - FindObjectOfType는 씬 구조/비활성 오브젝트/실행 순서에 따라 실패할 수 있음.
/// - 앵커 오브젝트에 확실히 연결해두면 안정적이다.
/// </summary>
public class AnnotatorAnchor : MonoBehaviour
{
    public static AnnotatorAnchor Instance { get; private set; }

    [Tooltip("IOverlayAnnotator 구현체(예: UIToolkitOverlayAnnotatorOptimized)를 연결하세요.")]
    [SerializeField] private MonoBehaviour annotatorBehaviour;

    public IOverlayAnnotator Annotator => annotatorBehaviour as IOverlayAnnotator;

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
