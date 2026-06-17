using UnityEngine;

public interface IStageValue
{
    public int stage { get; set; }
    public HugeInt value { get; set; }

    public void WhileBeHit(int _stage, HugeInt _value);

    public void SetValue(int _stage, HugeInt _value)
    {
        stage = _stage;
        value = _value;
    }

    public HugeInt Hit(int _stage, HugeInt _value)
    {
        HugeInt cost = 0;
        if (stage == _stage) return cost;
        if (value > _value)
        {
            cost = _value;
            WhileBeHit(_stage, cost);
            value -= cost;
        }
        else
        {
            cost = value;
            WhileBeHit(_stage, cost);
            value = 0;
        }
        return cost;
    }

    public void Heal(int _stage, HugeInt _value)
    {
        value += _value;
        WhileBeHit(_stage, -_value);
    }
}
