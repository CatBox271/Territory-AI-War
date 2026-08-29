using UnityEngine;
using TMPro;

public class MessageDisplayer : MonoBehaviour
{
    public GameObject MonoText;

    [Header("圆形布局")]
    public float circleRadius = 2f;
    [Header("字体显示速度")]
    public float charDuration = 0.1f;
    public float charInterval = 0.01f;
    private TextMeshPro _text;
    private FieldArrivalModifier _anim;
    private Transform _textPool;
    private Transform textPool => _textPool ??= GameObject.FindGameObjectWithTag("TextPool").transform;

    private void OnDestroy()
    {
        if (_text != null)
            Destroy(_text.gameObject);
    }

    public bool Say(string content)
    {
        if (_anim != null && _anim.IsPlaying)
            return false;

        if (_text == null)
        {
            GameObject go = Instantiate(MonoText, textPool);
            _text = go.GetComponent<TextMeshPro>();
            _anim = go.GetComponent<FieldArrivalModifier>();
        }

        _text.text = content;
        _text.alpha = 1f;

        _anim.charDuration = charDuration;
        _anim.charInterval = charInterval;

        Vector3 circlePoint = CircleIntersection(transform.position);
        _text.transform.position = circlePoint;
        _anim.SetStartPoint(transform.position);
        _text.gameObject.SetActive(true);
        _anim.Play();

        return true;
    }

    Vector3 CircleIntersection(Vector3 center)
    {
        Vector3 dir = center.normalized;
        if (dir.sqrMagnitude < 0.0001f) dir = Vector3.up;
        return center - dir * circleRadius;
    }
}
