using System.Collections;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEngine;

public class MarbleManager : MonoBehaviour
{
    public enum UpgradeChoice { Marble = 1, Turret = 2, Shield = 3 }

    public static MarbleManager Instance;

    [Header("Prefabs & Refs")]
    public GameObject MarbleOb;
    public Transform Shooter;
    public Transform spawnArea;

    [Header("开局出生点（随机一次后固定）")]
    [Tooltip("勾一下：立刻按 spawnArea 重新随机一整组开局出生点，写进下面那张表，并把勾自动去掉。结果要留下来记得保存场景。")]
    public bool randomizeOpeningSpawns = false;
    [Tooltip("开局出生点表：按出生顺序取用（每轮每队一个）。为空或不够用时，剩下的退回原来的随机。")]
    public List<Vector3> openingSpawnPositions = new();

    [Header("Settings")]
    public int initialMarbleCount = 3;
    public float initialSpawnDelay = 0.3f;
    public uint initialValueExponent = 10;
    public uint FirstValueExponent = 20;
    public uint startValueExponent = 10;
    public HugeInt maxValue;
    public float gravity = 0.1f;

    private int teamCount;
    private List<GameObject>[] teamMarbleObs;

    private Shooter shooterComp;

    // 升级与能量的**数值**统一放在 MapConfig 上
    // （upgradeCost / upgradeCostGrowth / upgradePerEmptySlotPerSecond / moveEnergyCost / whisperEnergyCost），
    // 这里只保留运行期进度。

    // 参与空槽升级的 AI 阵营，以及各自的升级进度。
    private readonly HashSet<int> aiStages = new();
    private readonly Dictionary<int, float> upgradeProgress = new();
    private readonly Dictionary<int, float> upgradeCosts = new();

    // 已升级次数：阵营 -> 次数（用于在升级选择时告诉 AI“你已经选过几次”，避免总选同一个）
    private readonly Dictionary<int, int> marbleUpgradeCount = new();
    private readonly Dictionary<int, int> turretUpgradeCount = new();
    private readonly Dictionary<int, int> shieldUpgradeCount = new();

    private void Awake()
    {
        Instance = this;
    }

    /// <summary>登记参与空槽升级机制的 AI 阵营。</summary>
    public void RegisterAIStage(int aiStage)
    {
        if (aiStage <= 0) return;
        aiStages.Add(aiStage);
        if (!upgradeProgress.ContainsKey(aiStage))
        {
            upgradeProgress[aiStage] = 0f;
            upgradeCosts[aiStage] = MapConfig.Instance != null ? MapConfig.Instance.upgradeCost : 2f;
        }
    }
    void Start()
    {
        if (Shooter != null)
            shooterComp = Shooter.GetComponent<Shooter>();

        teamCount = MapConfig.Instance.teamColors.Count - 1;
        teamMarbleObs = new List<GameObject>[teamCount + 1];

        for (int stage = 1; stage <= teamCount; stage++)
            teamMarbleObs[stage] = new List<GameObject>();

        StartCoroutine(SpawnInitial());
    }

    bool first = true;
    IEnumerator SpawnInitial()
    {
        int spawnIndex = 0;
        for (int i = 0; i < initialMarbleCount; i++)
        {
            for (int stage = 1; stage <= teamCount; stage++)
                SpawnAndLaunchAt(stage, NextOpeningSpawn(ref spawnIndex));
            yield return new WaitForSeconds(initialSpawnDelay);
        }
        first = false;
    }

    /// <summary>开局出生点：优先用表里存好的固定点（勾一次随机后就不再变），表用完才退回随机。</summary>
    Vector3 NextOpeningSpawn(ref int index)
    {
        if (openingSpawnPositions != null && index < openingSpawnPositions.Count)
            return openingSpawnPositions[index++];
        index++;
        return GetSpawnPosition();
    }

    void Update()
    {
        UpdateEmptySlotUpgrade();
    }

    /// <summary>
    /// 收尾阶段开关（由 AIAgent.TickSoloWrapUp 置 true、每局开局在 AIAgent.StartCycle 里复位）：
    /// 置上之后空槽升级进度不再累加、也不会再弹升级三选一（这一局已经定了，升级没有意义）。
    /// </summary>
    public static bool UpgradesStopped;

    /// <summary>空槽升级进度：AI 模式按空槽数累计；非 AI 模式每个弹珠固定每秒 +1。</summary>
    private void UpdateEmptySlotUpgrade()
    {
        MapConfig config = MapConfig.Instance;
        if (config == null || config.teamProps == null) return;
        if (config.upgradePerEmptySlotPerSecond <= 0f || config.upgradeCost <= 0f) return;

        if (config.useAIDecision)
        {
            foreach (int aiStage in aiStages)
            {
                if (aiStage < 0 || aiStage >= config.teamProps.Length) continue;
                List<PropEntry> props = config.teamProps[aiStage];
                if (props == null) continue;

                int emptySlots = Mathf.Max(0, config.propLimit - props.Count);
                if (emptySlots <= 0) continue;

                AdvanceUpgrade(aiStage, emptySlots * config.upgradePerEmptySlotPerSecond * Time.deltaTime);
            }
        }
        else
        {
            // AI 模式关闭：当前每个弹珠的升级进度固定每秒 +1
            for (int stage = 1; stage <= teamCount; stage++)
                AdvanceUpgrade(stage, Time.deltaTime);
        }
    }

    private void AdvanceUpgrade(int stage, float gain)
    {
        if (gain <= 0f) return;
        // 收尾阶段（只剩一家 + 场上没威胁 + 过了 3 轮）：停升级——进度不再涨、也不再弹三选一。
        if (UpgradesStopped) return;

        if (!upgradeProgress.ContainsKey(stage))
        {
            upgradeProgress[stage] = 0f;
            upgradeCosts[stage] = MapConfig.Instance != null ? MapConfig.Instance.upgradeCost : 2f;
        }

        float progress = upgradeProgress[stage];
        progress += gain;
        float fallbackCost = MapConfig.Instance != null ? MapConfig.Instance.upgradeCost : 2f;
        float cost = upgradeCosts.TryGetValue(stage, out float c) ? c : fallbackCost;

        while (progress >= cost)
        {
            progress -= cost;
            cost *= MapConfig.Instance != null ? MapConfig.Instance.upgradeCostGrowth : 2.4f;

            UIMarbleUpgrade.Instance.ShowUpgrade(stage);
            if (MapConfig.Instance.useAIDecision && AIAgent.Instance != null)
                StartCoroutine(AIUpgradeSequence(stage));
            else
                StartCoroutine(UpgradeSpawnSequence(stage));
        }

        upgradeCosts[stage] = cost;
        upgradeProgress[stage] = progress;
    }

    /// <summary>
    /// useAI 模式下：询问 AI 选择升级，选择后执行对应升级。
    /// 顺序固定为「**先请求 → 再开舞台 → 演完才让升级生效**」：
    /// 请求在舞台没开的时候发（录制暂停，等待不进视频），结果回来再开 UpgradeChoiceScene 演「揭晓」，
    /// 演出播完才 ApplyUpgradeChoice —— 这样炮塔升级的连线与飘字仍然留在战场上播。
    /// 反过来（先开舞台、在演出里请求）会让演出撞上录制的暂停/恢复：非实时录制下 AVPro 的
    /// ResumeCapture 会把 Time.timeScale 顶回 1，舞台还没播完战场就活了。
    /// 另外「演完」指的是**舞台彻底收工**（队列跑完、关场淡出也走完），不是这一场演完就够：
    /// 一次升级多格会连播好几场，连线是 Overlay 的 UI，会压在还开着的舞台上。
    /// </summary>
    IEnumerator AIUpgradeSequence(int stage)
    {
        // 已出局不再走升级流程：死亡时道具栈被清空、空槽反而最多，不拦就会对着死者再演一场升级选择
        if (WhisperManager.IsDead(stage)) yield break;

        if (AIAgent.Instance == null)
        {
            ApplyUpgradeChoice(stage, UpgradeChoice.Marble);
            yield break;
        }

        if (StoryTeller.CanPlay)
        {
            Task<UpgradeChoiceResult> resultTask = AIAgent.Instance.UpgradeChoiceRequestAsync(stage);
            while (!resultTask.IsCompleted)
                yield return null;

            UpgradeChoiceResult result = null;
            if (resultTask.IsFaulted || resultTask.IsCanceled)
                Debug.LogWarning($"[Upgrade] stage {stage} 升级选择请求失败，按 +1 弹珠演。{resultTask.Exception}");
            else
                result = resultTask.Result;

            var scene = new UpgradeChoiceScene(stage, result);
            StoryTeller show = StoryTeller.Instance;
            show.Play(scene);
            while (!scene.Finished)
                yield return null;

            // 演出播完 ≠ 舞台收工：一次升级多格会连播好几场（队列里还有下一场），而且舞台相机正在淡出、
            // timeScale 也还没放开。连线是 Screen Space Overlay 的 UI（canvas 的 m_RenderMode = 0），
            // 画在所有相机之上 —— 舞台没关完就 ApplyUpgradeChoice，那条线就直接压在还在演的舞台上，
            // 看上去就是"升级的线穿透舞台、显示在最上面"。
            // 等队列彻底跑完（IsPerforming=false：关场淡出也走完、画面已经还给战场）再启动。
            // 舞台期间 timeScale 是 0，所以计时得用 unscaled，顺带加个 30 秒兜底免得队列万一卡住把升级挂死。
            float waited = 0f;
            while (show.IsPerforming && waited < 30f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            ApplyUpgradeChoice(stage, (UpgradeChoice)scene.Choice);
            yield break;
        }

        // 演出关掉时的旧路径：直接问 AI，不进舞台
        Task<int> choiceTask = AIAgent.Instance.RequestUpgradeChoiceAsync(stage);
        while (!choiceTask.IsCompleted)
            yield return null;

        int choice = 1;
        if (choiceTask.IsFaulted || choiceTask.IsCanceled)
        {
            Debug.LogWarning($"[Upgrade] stage {stage} 升级选择请求失败，默认 +1 弹珠。{choiceTask.Exception}");
        }
        else
        {
            choice = choiceTask.Result;
        }
        ApplyUpgradeChoice(stage, (UpgradeChoice)choice);
    }

    private static string UpgradeChoiceText(int choice)
    {
        switch (choice)
        {
            case 2: return "炮塔升级";
            case 3: return "护盾升级";
            default: return "弹珠+2";
        }
    }

    public void ApplyUpgradeChoice(int stage, UpgradeChoice choice)
    {
        switch (choice)
        {
            case UpgradeChoice.Turret:
                BumpUpgradeCount(turretUpgradeCount, stage);
                StartCoroutine(TurretUpgradeSequence(stage, UpgradeChoice.Turret));
                break;
            case UpgradeChoice.Shield:
                BumpUpgradeCount(shieldUpgradeCount, stage);
                StartCoroutine(TurretUpgradeSequence(stage, UpgradeChoice.Shield));
                break;
            default:
                BumpUpgradeCount(marbleUpgradeCount, stage);
                StartCoroutine(UpgradeSpawnSequence(stage));
                break;
        }
    }

    private static void BumpUpgradeCount(Dictionary<int, int> table, int stage)
    {
        table[stage] = (table.TryGetValue(stage, out int c) ? c : 0) + 1;
    }

    /// <summary>查询某阵营已经升级过几次（额外弹珠 / 炮塔强化 / 护盾强化）。</summary>
    public bool TryGetUpgradeCounts(int stage, out int marble, out int turret, out int shield)
    {
        marble = marbleUpgradeCount.TryGetValue(stage, out int m) ? m : 0;
        turret = turretUpgradeCount.TryGetValue(stage, out int t) ? t : 0;
        shield = shieldUpgradeCount.TryGetValue(stage, out int s) ? s : 0;
        return aiStages.Contains(stage) || marble + turret + shield > 0;
    }

    /// <summary>某阵营当前还活着的弹珠数（升级三选一里写"当前 → 升级后"要用）。</summary>
    public int GetMarbleCount(int stage)
    {
        if (teamMarbleObs == null || stage <= 0 || stage >= teamMarbleObs.Length) return 0;
        List<GameObject> list = teamMarbleObs[stage];
        if (list == null) return 0;
        int count = 0;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] == null) { list.RemoveAt(i); continue; }
            count++;
        }
        return count;
    }

    /// <summary>炮塔/护盾升级：从升级槽连线到炮塔，线到后再应用升级并弹上升文本。</summary>
    IEnumerator TurretUpgradeSequence(int stage, UpgradeChoice choice)
    {
        if (!Towel.AllTowel.TryGetValue(stage, out Towel towel) || towel == null)
            yield break;

        Vector3 target = towel.transform.position;
        UIMarbleUpgrade ui = UIMarbleUpgrade.Instance;
        if (ui != null)
            yield return PlayLineToTarget(stage, target);

        if (choice == UpgradeChoice.Turret) towel.ApplyTurretUpgrade();
        else towel.ApplyShieldUpgrade();

        string label = choice == UpgradeChoice.Turret ? "炮塔升级" : "护盾升级";
        towel.ShowTip(label);
    }

    IEnumerator PlayLineToTarget(int stage, Vector3 target)
    {
        UIMarbleUpgrade ui = UIMarbleUpgrade.Instance;
        if (ui == null) yield break;

        bool arrived = false;
        ui.PlayLineTo(stage, target, () => arrived = true);
        float waited = 0f;
        while (!arrived)
        {
            waited += Time.deltaTime;
            if (waited > 3f) break;
            yield return null;
        }
    }

    IEnumerator UpgradeSpawnSequence(int stage)
    {
        Vector3 target = GetSpawnPosition();
        UIMarbleUpgrade ui = UIMarbleUpgrade.Instance;
        if (ui != null)
        {
            bool arrived = false;
            ui.PlayLineTo(stage, target, () => arrived = true);
            float waited = 0f;
            while (!arrived)
            {
                waited += Time.deltaTime;
                if (waited > 3f) break;
                yield return null;
            }
        }
        // 额外弹珠升级：一次给**两颗** —— 第一颗落在连线指的那个点（演出指着它），
        // 第二颗另取一个出生点，免得两颗叠在一起抖。
        SpawnAndLaunchAt(stage, target);
        SpawnAndLaunchAt(stage, GetSpawnPosition());
    }


    /// <summary>查询指定阵营当前的弹珠升级进度与下一次升级所需值。</summary>
    public bool TryGetUpgradeInfo(int stage, out float progress, out float cost)
    {
        progress = upgradeProgress.TryGetValue(stage, out float p) ? p : 0f;
        cost = upgradeCosts.TryGetValue(stage, out float c)
            ? c
            : (MapConfig.Instance != null ? MapConfig.Instance.upgradeCost : 2f);
        return aiStages.Contains(stage) || upgradeProgress.ContainsKey(stage);
    }

    /// <summary>当前可用的升级能量（即空槽升级进度）。</summary>
    public float GetUpgradeEnergy(int stage)
    {
        return upgradeProgress.TryGetValue(stage, out float p) ? p : 0f;
    }

    /// <summary>退还升级能量（悄悄话被系统调配/终止时用）。</summary>
    public void AddUpgradeEnergy(int stage, float amount)
    {
        if (amount <= 0f) return;
        upgradeProgress[stage] = GetUpgradeEnergy(stage) + amount;
    }

    /// <summary>尝试扣除升级能量：足够则扣掉并返回 true；不够则返回 false 且不扣。</summary>
    public bool TrySpendUpgradeEnergy(int stage, float amount)
    {
        if (amount <= 0f) return true;
        float progress = GetUpgradeEnergy(stage);
        if (progress < amount) return false;
        upgradeProgress[stage] = progress - amount;
        return true;
    }

    /// <summary>
    /// 需要花升级点数的动作：移动 / 悄悄话（**公开发言不花点数**：它是每回合必然要说的话，扣了就只能咽回去，视频里就没台词了）。
    /// 每种动作各自记"用了几次"，价格 = 基础价 × 涨价倍率^次数（倍率在 MapConfig.actionCostGrowth）。
    /// </summary>
    public enum EnergyAction { Move, Whisper }

    private readonly Dictionary<int, int>[] actionUseCount =
    {
        new Dictionary<int, int>(),   // Move
        new Dictionary<int, int>(),   // Whisper
    };

    /// <summary>这个阵营这个动作**这一次**的价格（已含涨价）。</summary>
    public float GetActionEnergyCost(int stage, EnergyAction action)
    {
        MapConfig cfg = MapConfig.Instance;
        float baseCost = action switch
        {
            EnergyAction.Move => cfg != null ? cfg.moveEnergyCost : 25f,
            _ => cfg != null ? cfg.whisperEnergyCost : 25f,
        };
        float growth = cfg != null ? cfg.actionCostGrowth : 1f;
        int used = actionUseCount[(int)action].TryGetValue(stage, out int c) ? c : 0;
        // growth <= 1 就是不涨价；used 很大时 Pow 可能溢出，用 double 再夹一下
        double factor = growth > 1f ? System.Math.Pow(growth, used) : 1d;
        if (double.IsInfinity(factor) || factor > 1e9) factor = 1e9;
        return baseCost * (float)factor;
    }

    /// <summary>这个阵营这个动作已经用过几次（用于情报/提示词展示下次价格）。</summary>
    public int GetActionUseCount(int stage, EnergyAction action)
        => actionUseCount[(int)action].TryGetValue(stage, out int c) ? c : 0;

    /// <summary>
    /// 花一次这个动作的能量：够则扣除、累计次数、返回 true；不够返回 false 且什么都不改。
    /// 价格由 GetActionEnergyCost 现算，所以调用方不要自己算价格，直接用这里的返回值。
    /// </summary>
    public bool TrySpendActionEnergy(int stage, EnergyAction action, out float paid)
    {
        paid = GetActionEnergyCost(stage, action);
        if (!TrySpendUpgradeEnergy(stage, paid)) return false;
        actionUseCount[(int)action][stage] = GetActionUseCount(stage, action) + 1;
        return true;
    }

    /// <summary>退还一次这个动作的能量（悄悄话被系统调配终止时用），并把次数退回去，价格回到用之前。</summary>
    public void RefundActionEnergy(int stage, EnergyAction action, float amount)
    {
        AddUpgradeEnergy(stage, amount);
        Dictionary<int, int> table = actionUseCount[(int)action];
        if (table.TryGetValue(stage, out int c) && c > 0) table[stage] = c - 1;
    }
    /// <summary>供空槽升级等系统外部调用，给指定阵营额外生成并发射一个弹珠。</summary>
    public void SpawnMarbleForStage(int stage)
    {
        SpawnAndLaunch(stage);
    }

    void SpawnAndLaunch(int stage)
    {
        SpawnAndLaunchAt(stage, GetSpawnPosition());
    }

    void SpawnAndLaunchAt(int stage, Vector3 pos)
    {
        if (MarbleOb == null) return;
        if (!Towel.AllTowel.ContainsKey(stage)) return;

        GameObject ob = Instantiate(MarbleOb, pos, Quaternion.identity);

        Marble m = ob.GetComponent<Marble>();
        if (m != null)
        {
            m.stage = stage;
            m.SetInitialValue(first? FirstValueExponent: startValueExponent);
            //注册到InformGeter供AI数据收集
            InformGetter.AddMarble(stage, m);
        }

        var col = ob.GetComponent<Collider2D>();
        foreach (var teamob in teamMarbleObs[stage])
        {
            //同队无碰撞
            Physics2D.IgnoreCollision(col, teamob.GetComponent<Collider2D>());
        }

        teamMarbleObs[stage].Add(ob);
    }

    Vector3 GetSpawnPosition()
    {
        if (spawnArea != null)
        {
            Vector3 center = spawnArea.position;
            Vector3 half = spawnArea.lossyScale * 0.5f;
            float x = Random.Range(center.x - half.x, center.x + half.x);
            float y = Random.Range(center.y - half.y, center.y + half.y);
            return new Vector3(x, y, center.z);
        }
        return Shooter != null ? Shooter.position : transform.position;
    }

#if UNITY_EDITOR
    /// <summary>
    /// 编辑器里勾上 randomizeOpeningSpawns：当场按 spawnArea 随机一整组开局出生点写进表里，然后把这个勾去掉。
    /// 表是世界坐标，之后每次开局都照它出生（不再每次随机）；要换一批就再勾一次。
    /// </summary>
    private void OnValidate()
    {
        if (!randomizeOpeningSpawns) return;
        randomizeOpeningSpawns = false;
        RandomizeOpeningSpawns();
        UnityEditor.EditorUtility.SetDirty(this);
    }
#endif

    /// <summary>随机一整组开局出生点（每轮每队一个），整表覆盖 openingSpawnPositions。</summary>
    private void RandomizeOpeningSpawns()
    {
        int teams = ResolveTeamCount();
        if (openingSpawnPositions == null) openingSpawnPositions = new List<Vector3>();
        openingSpawnPositions.Clear();
        for (int i = 0; i < Mathf.Max(1, initialMarbleCount) * teams; i++)
            openingSpawnPositions.Add(GetSpawnPosition());
    }

    /// <summary>队伍数（0 号阵营是无主，要减掉）。编辑器里 MapConfig.Instance 还没赋值，就回场景里找它。</summary>
    private int ResolveTeamCount()
    {
        MapConfig config = MapConfig.Instance;
#if UNITY_EDITOR
        if (config == null && !Application.isPlaying) config = FindObjectOfType<MapConfig>();
#endif
        if (config != null && config.teamColors != null && config.teamColors.Count > 1)
            return config.teamColors.Count - 1;
        return 4;
    }

    public void OnTeamDeath(int stage)
    {
        if (stage <= 0 || stage > teamCount) return;
        var list = teamMarbleObs[stage];
        if (list == null) return;
        foreach (var ob in list)
            if (ob != null) Destroy(ob);
        list.Clear();
    }

    public Shooter GetShooter()
    {
        return Shooter != null ? Shooter.GetComponent<Shooter>() : null;
    }
}
