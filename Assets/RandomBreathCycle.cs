using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RandomBreathCycle : StateMachineBehaviour
{
    override public void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
    {
        animator.SetFloat("breathRandom", Random.Range(0.9f, 1.1f));
    }
}
