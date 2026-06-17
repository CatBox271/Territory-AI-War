using System.Collections;
using UnityEngine;
using TMPro;

public class MessageDisplayer : MonoBehaviour
{
    public GameObject MonoText;

    public struct MonoTextOb
    {
        public GameObject Ob;
        public TextMeshPro text;
        public FieldArrivalModifier animator;
    }

    [Header("圆形布局")]
    public float circleRadius = 2f;

    private MonoTextOb _current;

    private void Start()
    {
        Say("HelloWorld");
    }
    private Transform _textPool;
    private Transform textPool => _textPool ??= GameObject.FindGameObjectWithTag("TextPool").transform;
    public void Say(string content)
    {
        // 把旧的淡出
        if (_current.Ob != null)
        {
            _current.animator.PlayFallOut();
            var old = _current;
            StartCoroutine(DestroyAfterFall(old));
        }

        // 在当前 transform 下生成新 Ob
        GameObject go = Instantiate(MonoText, textPool);
        var text = go.GetComponent<TextMeshPro>();
        var anim = go.GetComponent<FieldArrivalModifier>();

        text.text = content;
        text.alpha = 1f;

        Vector3 circlePoint = CircleIntersection(transform.position);
        go.transform.position = circlePoint;
        anim.SetStartPoint(transform.position);
        go.SetActive(true);
        anim.Play();

        _current = new MonoTextOb { Ob = go, text = text, animator = anim };
    }

    IEnumerator DestroyAfterFall(MonoTextOb old)
    {
        yield return new WaitForSeconds(old.animator.TotalFallDuration);
        if (old.Ob != null) Destroy(old.Ob);
    }

    Vector3 CircleIntersection(Vector3 center)
    {
        Vector3 dir = center.normalized;
        if (dir.sqrMagnitude < 0.0001f) dir = Vector3.up;
        return center - dir * circleRadius;
    }
}
