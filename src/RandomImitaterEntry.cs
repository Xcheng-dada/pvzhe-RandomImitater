using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「随机模仿者」Mod 的托管运行时入口。
///
/// ── 需求 ───────────────────────────────────────────────────────
/// **不改动**游戏任何现有内容，只**新增**一个植物 `RandomImitater`：
///   种下之后随机开出（从「全部植物卡 + 全部僵尸卡」里抽一张）：
///     · 默认 65% 开出随机植物；
///     · 默认 35% 开出随机僵尸（敌对，会朝玩家的房子走）。
///   植物与僵尸**各自等概率**：每张植物、每只僵尸中奖率相同，与卡池分类无关。
///   ★ 植物概率与「彩色化」开关可在游戏内「Mod 管理 → 配置」里实时调整，保存即生效。
///
/// ── 实现原理（复用游戏内置能力，零改动）───────────────────────
/// 游戏本体的 `TowerDefensePlantImitater.Explode()` 已经写好了这段逻辑：
///     var bank = TowerDefenseManager.GetPacketBankData(this.packetBank);
///     var name = (bank.GetCategory("White") + bank.GetCategory("Original")).PickRandom();
///     var cfg  = TowerDefenseManager.GetPacketConfig(name);
///     if (cfg.characterConfig is TowerDefensePlantConfig)  cfg.Plant(gridPos, ...);
///     if (cfg.characterConfig is TowerDefenseZombieConfig) { var z = cfg.Create(...);
///         GetCharacterNode().AddChild(z); z.instance.wakeUp = true; ... }
/// ⇒ **"随机变植物或僵尸"就是这个类的原生行为**，唯一的前提是它读的那个卡池
///   （`packetBank`）里**同时**含有植物卡和僵尸卡。
///
/// 而游戏的卡池全部来自**打包时**的 `Asset/Config/PacketBank/PacketBankResource.json`
/// （`ResourceManager.BuildExpandedPacketBanks()` 只读这一个文件，且在 Mod 加载**之前**
///  就发布完毕 ⇒ Mod 无法用纯资源方式新增卡池）。而且内置卡池里
///  僵尸一律只出现在 `Zombie` 分类，`White`/`Original` 分类里只有植物
///   ⇒ 直接用内置卡池**永远抽不到僵尸**。
///
/// 所以本 Mod 的唯一动作：**运行时**往 `ResourceManager.TOWERDEFENSE_PACKETBANKS`
/// 里注册一个自定义卡池 `RandomImitaterMixed`，把 `White` 分类填成
/// 「全部植物 + 全部僵尸」的混合列表；角色场景的 `packetBank` 指向它即可。
///
/// ── 铁律 ───────────────────────────────────────────────────────
/// `Initialize` / `OnAllModsLoaded` / `Shutdown` **一律不许抛**：
/// 抛出去 ⇒ ModLoader 判定运行入口失败 ⇒ **整包无条件回滚**。
/// </summary>
public sealed class RandomImitaterEntry : IXWModRuntimeEntry
{
	private const string P = "[RandomImitater] ";

	/// <summary>本 Mod 新增的植物 key（= 目录名 = 场景名 = config.name = packet.saveKey）。</summary>
	internal const string MyKey = "RandomImitater";

	/// <summary>运行时注册的混合卡池名 —— 新植物场景里的 `packetBank` 指向它。</summary>
	private const string CustomBank = "RandomImitaterMixed";

	/// <summary>
	/// 当前这版混合卡池里的**植物** key 名单（由 <c>EnsureMixedBank</c> 在注册成功后刷新）。
	/// `RandomImitater.RiExplode` 据此在变身窗口内临时放宽这些植物的地形要求，
	/// 让「本格种不下的植物」不会被原生重抽循环剔掉（见该方法的注释）。
	/// </summary>
	internal static readonly HashSet<string> PoolPlantKeys =
		new HashSet<string>(StringComparer.Ordinal);

	/// <summary>植物卡来源卡池（干净的全植物池，不含保龄球等特殊卡）。</summary>
	private const string PlantSrcBank = "GeneralPlant";

	/// <summary>僵尸卡来源卡池（= GeneralZombie + ExtendZombie）。</summary>
	private const string ZombieSrcBank = "TotalZombie";

	/// <summary>
	/// ★ 出植物的概率（0~1）。剩余概率给僵尸。
	///
	/// 当前 0.65 ⇒ 65% 植物 / 35% 僵尸，**与两边卡池大小无关**。
	/// 之所以要显式定这个值：卡池里植物 325 张、僵尸 290 只，如果直接把两边
	/// 各放一次混成列表随机抽，实际概率就是 325:290 ≈ 53%:47% —— 那是「卡池规模的
	/// 副作用」，不是有意设计。这里靠**同类卡重复相同次数**来控比例（见 BuildWeighted），
	/// 且组内严格等概率：每张植物、每只僵尸中奖率一致，不因白卡数量多就更容易出。
	///
	/// ★ 现在这是**默认值**，玩家可在游戏内「Mod 管理 → 配置」里实时改
	///   （设置键 <see cref="KeyPlantChance"/>，int 百分比 0~100）。
	///   改完立刻生效：本 Mod 每帧比对一次，变化就重建混合卡池（见 DriveSettingsWatch）。
	///
	/// ✅ `plantGridType = [2,6,5,3,1,10,8,9]`（GROUND/POT/LILYPAD/WATER/SOIL/BRICK/GRAVESTONE/CRATER）：
	///   · 格子的地形是**多层**的（cell.slot 的键 = config.gridType，Init 里逐层建槽），
	///     草地是 GROUND(2)、泥地是 SOIL(1)、砖地是 BRICK(10)、水面是 WATER(3)，
	///     所以列表必须把全部地面层都写进去，缺一个那种地形就种不下去（1.7.0 漏了 1/3/10）。
	///   · IL_0d1c 的第三轮遍历：本格任意一层被列表包含且槽位为空就放行 ⇒ 裸地/水面/泥地/砖地都能种。
	///   · IL_0404 的第一轮遍历遇墓碑含 8、遇弹坑含 9 ⇒ 都能下。
	///   · IL_0d82 收尾调用 GetCharacterWhoHasGridType，只认 override != 0 且落在列表里的占位者；
	///     POT/LILYPAD 的 override 是 6/5，命中则放行 ⇒ 容器上可再种；普通植物 override=0 且
	///     占满其地形列表与本格相交的全部槽位 ⇒ 拦得住。
	///   ⚠️ 但这条「拦得住」在**格上同时站着地形载体**时会失效：墓碑(8)/弹坑(9)的 override
	///     也落在本列表里，IL_0d82 一命中就整体放行，不看同一格上还有没有别的植物
	///     ⇒ 空墓碑要放行就必须含 8/9，含了 8/9 就拦不住叠种，数据层面无解。
	///     补上这道拦截的是 <c>RegisterPlacementGate</c>：走宿主的种植扩展点，
	///     在宿主结论之上追加「这格已站着实体植物 ⇒ 否决」。
	///   · AIR(4) 不写进列表：浮空咖啡豆 (override=0) 不占 GROUND 槽，靠 IL_0d1c 的空槽判断放行 ⇒ 咖啡伞在格上也种得下，
	///     同时避免「任何格子因 AIR 而强制通过」导致实体植物无法拦截。
	/// 自定义的 `RandomImitater.RiExplode()` 会**先把自己从格子上腾走**
	/// （`Destroy(false)` 同步清空 slot），再交给基类抽卡/落点校验，
	/// 所以校验看到的一格是空地，植物不会被自己挡掉。
	/// （旧版必须留 [-1] 的原因就是没有这一步 —— 见 RandomImitater 类注释。）
	/// </summary>
	private const double DefaultPlantChance = 0.65;

	/// <summary>
	/// ★ 阳光消耗的默认值与上限。
	///
	/// 默认 0（免费）与卡牌设计一致：这张卡有概率开出敌对僵尸，
	/// 再收阳光就太苛刻。上限 1500 交给游戏内配置项约束。
	/// </summary>
	private const int DefaultSunCost = 0;
	private const int MaxSunCost = 1500;

	/// <summary>僵尸在卡池里的分类名。</summary>
	private const string ZombieCat = "Zombie";

	/// <summary>
	/// 选卡界面「钻卡」页签的分类键。
	///
	/// 键名取自游戏选卡场景里预置的页签按钮（<c>PacketCategoryButton.category</c>，
	/// 其显示文字为「钻卡」）。游戏全部页签键为：
	///   White（白卡，用字段默认值故无显式赋值）、Gold（金卡）、Diamond（钻卡）、
	///   Colour（彩卡）、Star（星卡）、Original（原卡）、Item（物品卡）、
	///   GraveStone（障碍物）、Zombie（僵尸卡）、ModPlants（Mod 植物）。
	/// </summary>
	private const string DiamondCategory = "Diamond";

	/// <summary>
	/// 混合抽取列表的长度上限。
	///
	/// 列表越长，a/b 能凑出的比例越精细 —— 植物 325 / 僵尸 290 时，
	/// 29000 条（a=58、b=35）可让 65% **精确成立**。
	///
	/// 取 32768 是留足搜索空间的同时避免无谓膨胀：`PickRandom()` 只是随机取下标，
	/// 列表长度对随机性能没有影响；29000 个 Variant 约占 0.7 MB（Variant 为 24 字节），
	/// 对游戏可忽略。注意 `Explode()` 每次会 `GetCategory("White") + GetCategory("Original")`
	/// 复制一份该列表，29000 条的复制约在百微秒量级，同样可忽略。
	/// </summary>
	private const int MixedListMaxLen = 32768;

	/// <summary>
	/// ★ 是否允许抽出「BOSS 僵尸」与「巨人僵尸」。
	///
	/// 常量 true ⇒ BOSS / 巨人都留在随机池里，符合「完全随机」的设定，
	/// 不提供开关。
	/// </summary>
	private const bool AllowBossZombie = true;
	private const bool AllowGargantuar = true;

	// ─────────────── 游戏内设置键（与 mod.json 的 settings[].key 一一对应）───────────────
	// ★ 键名必须与清单里完全一致，否则读不到值、只能一直用默认值。
	//   清单由 build_mod.py 的 manifest() 生成，两处要同步改。

	/// <summary>出植物概率（整数百分比 0~100）。</summary>
	private const string KeyPlantChance = "plantChance";

	/// <summary>开出的植物/僵尸是否保持彩色（关掉游戏给模仿者产物加的黑白滤镜）。</summary>
	private const string KeyColorize = "colorizeResult";

	/// <summary>这张卡的阳光消耗（0~1500）。</summary>
	private const string KeySunCost = "sunCost";

	/// <summary>
	/// 植物卡池里需要排除的 key —— 只排除**自己**，避免开出自己导致递归开出。
	///
	/// ⚠️ 官方模仿者（`PlantLmitater` / `PlantImitaterW`）**不排除**：
	/// 它们也是普通植物卡，抽到它们是「完全随机」的应有之义。
	/// 它们读的是**自己场景上的** `packetBank`（= `GeneralPlant`），
	/// 不是我们的 `RandomImitaterMixed`，所以开出来不会回到本池 ⇒ 不存在递归。
	/// （早期版本误将 `PlantLmitater` 排除，等于让玩家永远抽不到官方模仿者。）
	/// </summary>
	private static readonly string[] ExcludeKeys =
	{
		MyKey,          // 自己（唯一需要排除的：防止无限递归开出）
	};

	/// <summary>诊断日志开关（改这个值需重新编译）。`internal` 以便伴随脚本类 `RandomImitater` 共用。</summary>
	internal static readonly bool EnableLog = true;

	/// <summary>供伴随脚本类打日志（它与入口是两个独立类，不能直接调彼此的私有方法）。</summary>
	internal static void LogStatic(string msg)
	{
		if (!EnableLog)
		{
			return;
		}
		try { GD.Print(P + msg); } catch { }
	}

	/// <summary>
	/// 「彩色化」开关的静态镜像，供伴随脚本类 <c>RandomImitater</c> 读取
	/// （它无法访问本入口的实例字段）。由 <see cref="ReadSettings"/> 维护。
	///
	/// true  = 清掉游戏给模仿者产物加的黑白滤镜，植物/僵尸保持原本配色；
	/// false = 保留游戏原生的黑白表现。
	/// </summary>
	internal static bool ColorizeResult = true;

	/// <summary>
	/// ★ 无限选取：让卡池里这张卡**每点一次就新增一张**到卡槽，直到卡槽满。
	///
	/// 游戏原生是「开关式」的（`TowerDefenseBattleFeaturePacketBank.PacketChoose`）：
	///     已选卡槽里能找到同 key 的卡 ⇒ 移除它（再点一次 = 取消）
	///     否则加入卡槽，并把卡池里那张卡 `alive = false`（变灰、不能再点）
	/// ⇒ 默认只能选一次。
	///
	/// 本 Mod 的做法：只对**本卡**接管 `OnPressed`（清掉游戏挂的处理器，换成我们自己的），
	/// 每次点按都调 `seedBank.AddPacket(cfg, false)` 新增一张，并强制卡池那张卡保持
	/// 可点（`alive = true` + `allowPressWhenUnavailable = true`）。
	/// 卡槽满时 `CanAddPacket()` 为 false，自然停止 —— 正好是「一直选到卡槽满」。
	/// </summary>
	private static readonly bool InfiniteSelect = true;

	private SceneTree _tree;
	private Callable _tick;
	private bool _started;
	private bool _bankOk;
	private int _diag;

	// ─────────────── 游戏内设置（即时生效）───────────────

	/// <summary>本 Mod 的配置对象；拿不到时为 null（此时一律用默认值）。</summary>
	private XWModSettings _settings;

	/// <summary>当前生效的出植物概率（0~1）。</summary>
	private double _plantChance = DefaultPlantChance;

	/// <summary>
	/// 上一帧从设置里读到的原始值快照，用于判断「有没有变」。
	/// 用原始 int/bool 而不是换算后的 double，避免浮点误差导致每帧都判定为变化。
	/// </summary>
	private int _rawPlantChance = int.MinValue;
	private bool _rawColorize;
	private int _rawSunCost = int.MinValue;

	/// <summary>设置读取是否已经成功过一次（用于日志）。</summary>
	private bool _settingsLogged;

	/// <summary>设置读取失败是否已提示过（只提示一次，避免刷屏）。</summary>
	private bool _settingsWarned;

	/// <summary>最近一次「因设置变化而重建卡池」的日志标记。</summary>
	private int _rebuildCount;

	/// <summary>「卡池里有 N 张覆盖型（紫卡）」这条提示只报一次。</summary>
	private static bool _coverPoolReported;

	/// <summary>
	/// 图鉴 <c>handbookDescribe</c> 的**原始文本**（首次读到时的样子）。
	/// 每次都从它重建，而不是在上一次改写结果上继续替换 ⇒ 反复切换概率不会文案漂移。
	/// null = 还没读过；判定细节见 <see cref="SyncCardPresentation"/>。
	/// </summary>
	private string _handbookOriginal;

	/// <summary>
	/// 本卡 <c>characterConfig.cost</c> 的**原始值**（首次读到时的样子），
	/// 供「恢复默认」时回到打包值而不是硬编码 0。int.MinValue = 还没读过。
	/// </summary>
	private int _costOriginal = int.MinValue;

	/// <summary>
	/// 本卡 <c>characterConfig.costNight</c> 的**原始值**。
	/// 只有它不是 -1（游戏用 -1 表示「夜图不单独定价」）时，才跟着 cost 一起同步；
	/// 见 <see cref="SyncCardPresentation"/> 里对 <c>GetCostBeforeModifiers</c> 的 IL 说明。
	/// </summary>
	private int _costNightOriginal = int.MinValue;

	/// <summary>图鉴 / 阳光消耗待同步。资源未就绪时置位，就绪后由 OnFrame 重试。</summary>
	private bool _presentationDirty;

	/// <summary>「没点中推车」的诊断已报条数（上限 3，见 MowerMissDiag）。</summary>
	private int _mowerMissLogged;

	/// <summary>上一帧右键是否按住，用于只在「按下那一下」触发的边沿检测。</summary>
	private bool _mowerRightDown;

	/// <summary>小推车触发链路的异常是否已提示过（只提示一次，避免刷屏）。</summary>
	private bool _mowerErrLogged;

	/// <summary>
	/// 点击判定的容差（单位：**格子**）。1.0 = 以推车绘制原点为中心、一整个格子大小
	/// 的方框内都算点中。行号另外单独严格比对，所以放大这个值也不会跨行误触。
	/// </summary>
	private const float MowerClickCellTolerance = 1.0f;

	// ─────────────── 钻卡页签注入状态 ───────────────

	/// <summary>
	/// 最近一次处理过的分类表对象实例 id（<c>packetBankData.GetInstanceId()</c>）。
	/// 换关时游戏会把它整体换成 WithPlants 的深拷贝 ⇒ id 变 ⇒ 重新注入一次。
	/// </summary>
	private ulong _diamondInjectedFor;

	/// <summary>注入成功是否已记过一次日志。</summary>
	private bool _diamondLogged;

	/// <summary>注入异常是否已提示过（只提示一次，避免刷屏）。</summary>
	private bool _diamondErrLogged;

	/// <summary>按卡实例 ID 记录已接管的卡池卡（仅用于日志去重）。</summary>
	private readonly HashSet<ulong> _hooked = new HashSet<ulong>();

	/// <summary>本帧扫到的卡片节点数 / 其中属于本卡的数量。</summary>
	private int _cardsSeen;
	private int _mineSeen;

	/// <summary>日志去重标记。</summary>
	private bool _hookLogged;
	private bool _hookErrLogged;

	/// <summary>
	/// ★ 「重选上次卡牌」接管：让本卡的多份（5 张等）在重选后**份数与顺序都精确还原**。
	///
	/// 塌陷点（IL 证据，见分析报告）：
	///   写入侧 <c>EmitChooseOverAsync</c> 是 foreach + <c>Array.Add</c>，**不去重**，
	///   5 张本卡会原样写成 5 个重复串 —— **存档本身是好的**。
	///   读取侧 <c>ReSelectButtonPressed</c> → <c>DeleteAllPacket()</c> → <c>PacketListChoose(整个数组)</c>，
	///   而 <c>PacketListChoose</c> 里 IL_0077 <c>seedBank.HasPacket(name)</c> + IL_007C <c>brtrue</c>
	///   把第 2..5 个同 key 条目**直接 continue 掉**（<c>HasPacket</c> 查的是
	///   <c>packetNameSet</c> 这个当 HashSet 用的字典，5 张只留下 1 个 key）。
	///   ⇒ 「选了 5 张随机模仿者，重选只回来 1 张」。
	///
	/// 为什么不能靠「原生跑完再补齐」（旧版做法，已废弃）：
	///   <c>AddPacket</c> 只能**追加到 packetList 末尾**。而玩家习惯「先选本卡、再补灰烬植物」，
	///   存档顺序是 <c>[R,R,R,R,R,灰1,灰2]</c>，原生只恢复出 <c>[R,灰1,灰2]</c>，
	///   再追加 4 张只会得到 <c>[R,灰1,灰2,R,R,R,R]</c> —— **顺序错了**。
	///   另外原生恢复走 Tween（异步入槽），补齐时机与它竞争，会出现「第一次点只补 1 张」。
	///
	/// 现在的做法（接管按钮 + 按存档顺序直接重建）：
	///   把按钮的 <c>OnPressed</c> **整体替换**成我们的处理器（原委托留作兜底）。
	///   点一次「重选」时：
	///     ① <c>DeleteAllPacket()</c> 清空；
	///     ② 按**存档顺序**逐项 <c>AddPacket(cfg, false)</c> + <c>StartInit()</c> + <c>alive = true</c>。
	///
	///   ⚠️ 这里**不能**用「逐张调用原生 <c>PacketListChoose([key])</c>」代替（踩过这个坑）：
	///   它会走到 <c>PacketChoose(poolCard)</c>，而那个方法是**开关式**的 ——
	///     IL_003D <c>FindSelectedPacket(saveKey)</c> → 找到同 key 的已选卡就**移除它**，
	///     找不到才加入。
	///   于是喂第 2 张本卡时，刚加进去的第 1 张会被删掉 ⇒ 5 张喂完只剩 1 张，等于没改。
	///   （清 <c>packetNameSet</c> 只能绕过 <c>HasPacket</c>，绕不过 <c>FindSelectedPacket</c>。）
	///
	///   <c>AddPacket</c> 则**没有任何**去重/开关守卫：<c>packetNameSet</c> 只被写入、不被查询，
	///   所以同一个 key 可以重复入槽，且每次都追加到 <c>packetList</c> 末尾 ⇒
	///   按存档顺序调用即可**精确还原份数与顺序**。
	///
	/// 代价：这条路径没有卡牌飞入动画（<c>CreateAnime</c> 是 Tween 异步入槽，
	///   正是旧版「第一次点只补 1 张」的根因），卡片直接出现在卡槽里。
	///   换取的是份数与顺序都精确 —— 这正是玩家要的。
	///
	/// 收窄原则：**只有存档里本卡 ≥2 张时才接管**；其余情况原样调用原生处理器，
	/// 玩家与其它卡的行为和没装 Mod 时完全一致。
	/// 任何异常都退回原生处理器 ⇒ 最坏情况 = 原生行为（只回来 1 张），**按钮不会失灵**。
	/// </summary>
	private static readonly bool ReselectTakeover = true;

	/// <summary>存档里「上次卡牌选择」的键名（与游戏 <c>EmitChooseOverAsync</c> 用的串一致）。</summary>
	private const string ReSlectKey = "PacketReSlect";

	/// <summary>IZM（僵尸模式）下的存档键名 —— 与游戏 <c>EmitChooseOverAsync</c> 的分支一致。</summary>
	private const string ReSlectKeyIzm = "ZombiePacketReSlect";

	/// <summary>
	/// 是否接管「保存/读取选卡分组」的**读取**侧（共 6 个分组）。
	///
	/// ★ 为什么也要接管：`LoadPacketGroup(id)` 与「重选上次卡牌」是**同一条**路径 ——
	///   IL_008a-IL_00a1：`seedBank.DeleteAllPacket()` + `PacketListChoose(存档)`。
	///   而 `PacketListChoose` 用按 saveKey 去重的 `packetNameSet` 判「已有」，
	///   所以分组里存了 5 张本卡，读回来也只会进 1 张（与重选完全相同的 bug）。
	///
	/// ★ 只接管**读取**：`SavePacketGroup(id)` 是直接遍历 `seedBank.packetList`
	///   把每张卡的 key 写进存档（IL_0006-IL_005d），**本来就能正确保存重复卡**
	///   ⇒ 保存侧不需要也不应该动。
	/// </summary>
	private static readonly bool GroupTakeover = true;

	/// <summary>分组存档键前缀（普通模式）：`PacketGroup1` … `PacketGroup6`。</summary>
	private const string GroupKeyPrefix = "PacketGroup";

	/// <summary>分组存档键前缀（IZM 模式）：`ZombiePacketGroup1` … `ZombiePacketGroup6`。</summary>
	private const string GroupKeyPrefixIzm = "ZombiePacketGroup";

	/// <summary>分组数量（游戏里就是 6 个）。</summary>
	private const int GroupCount = 6;

	/// <summary>最近一次扫到的本卡池卡（接管时用它判断界面是否就绪）。</summary>
	private TowerDefenseInGamePacketShow _myPoolCard;

	/// <summary>
	/// 被我们替换掉的**原生**「重选」处理器。
	/// 我们的处理器一旦出错（或判定不该接管），就原样调用它 ⇒ 退化为原生行为。
	/// </summary>
	private Delegate _reselectOriginal;

	/// <summary>日志去重标记。</summary>
	private bool _reselectLogged;
	private bool _reselectErrLogged;
	private bool _reselectTakeoverLogged;
	private bool _reselectRebuildLogged;

	/// <summary>
	/// 按下「重选」后**期望**的卡槽 key 序列（保持存档顺序）。
	/// 非 null 时 `DriveReselectReconcile()` 会盯几帧，核对是否被原生路径覆盖。
	/// </summary>
	private List<string> _reselectDesired;

	/// <summary>还要连续核对几帧（都一致就收工）。</summary>
	private int _reselectWatch;

	/// <summary>期望的本卡张数（核对的判据：只在本卡变少时才重建）。</summary>
	private int _reselectDesiredMine;

	/// <summary>本次已重建次数（上限 4，防止与原生来回拉锯）。</summary>
	private int _reselectAttempts;

	/// <summary>分组读取的日志去重标记。</summary>
	private bool _groupLogged;
	private bool _groupLogged2;
	private bool _groupErrLogged;

	/// <summary>
	/// 每个分组按钮当前的**原生** `OnLoadGroup` 底稿（按按钮实例 ID 记）。
	/// 与重选按钮同理：我们要整次重建，所以要拦住原生那条只会进 1 张的路径。
	/// </summary>
	private readonly Dictionary<ulong, Delegate> _groupOriginal = new Dictionary<ulong, Delegate>();

	// ================================================================ 生命周期

	public void Initialize(XWModRuntimeContext context)
	{
		try
		{
			string root = (context == null) ? "<null>" : context.PackageRoot;
			// ★ 配置对象要在这里抓住：它绑定的就是这个 Mod 的会话，
			//   后续 Initialize 返回后 context 不再可靠，而设置值是随时会变的。
			try
			{
				_settings = context == null ? null : context.Settings;
			}
			catch (Exception ex)
			{
				_settings = null;
				Log("拿不到 Mod 配置（将一直使用默认值）：" + ex.Message);
			}
			ReadSettings(true);
			RegisterPlacementGate(context);
			Log("初始化完成；PackageRoot=" + root + "。种下后将从植物+僵尸混合卡池里随机开出"
				+ (_placementGateOk ? "；占用格拦截已接上宿主扩展点。"
				                    : "；占用格拦截未接上宿主扩展点，按原生地形规则判定。"));
		}
		catch (Exception ex)
		{
			Swallow("Initialize", ex);
		}
	}

	public void OnAllModsLoaded()
	{
		try
		{
			if (_started)
			{
				return;
			}
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null)
			{
				Log("拿不到 SceneTree，本 Mod 不会生效（游戏其余部分不受影响）。");
				return;
			}
			_tick = Callable.From(new Action(OnFrame));
			_tree.Connect("process_frame", _tick);
			_started = true;
			Log("已挂载 process_frame（等待 ResourceManager 就绪后注册混合卡池）。");
		}
		catch (Exception ex)
		{
			Swallow("OnAllModsLoaded", ex);
		}
	}

	public void Shutdown()
	{
		try
		{
			if (_started && _tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Disconnect("process_frame", _tick);
			}
		}
		catch (Exception ex)
		{
			Swallow("Shutdown", ex);
		}
		finally
		{
			_started = false;
			try { RandomImitater.ClearColorizeTracking(); } catch { }
			// 本 Mod 关掉时若还压着 BOSS战 BGM，把关卡原曲放回去。
			try { RestoreBossBgm("卸载"); } catch { }
		}
	}

	// ================================================================ BOSS战 BGM

	/// <summary>
	/// 抽出的 BOSS 还活着时，背景音乐压成「BOSS战」（BrainiacManiac）。
	///
	/// 游戏本体**没有**「BOSS 出场换曲」这回事：`TowerDefenseBattleFeatureBGM` 只在
	/// 关卡生命周期（`GameEntry/GameStart/GameReady/GameFail/Destroy`）里被调用，
	/// 放哪首完全由关卡的 `backgroundMusic` 决定；BOSS 出场只播音效
	/// （`HydraulicShort`/`Hydraulic`，见 `TowerDefenseZombieBoss.SpawnEntered`）。
	/// 编辑器「背景音乐」下拉里那一项显示为「BOSS战」，对应配置
	/// `translate = BGM_BRAINIAC_MANIAC`、战斗曲 `flag1 = BrainiacManiac`
	/// （音频键 = `Asset/Audio/BGM/BrainiacManiac.ogg`）。
	/// 所以这里由本 Mod 自己接上：抽到 BOSS 就切，BOSS 死光再切回。
	///
	/// ★ 只认游戏自己当作 BOSS 的那三个类 —— `TowerDefenseBossHealthBarManager`
	///   只登记 `ZombieBoss` / `ZombieBossDave` / `ZombieBossEdgarII`（三套图标与颜色），
	///   走的是普通僵尸血条的**巨人**一家（`TowerDefenseZombieGargantuarBase` 及
	///   其十几个变体）不算 BOSS ⇒ 不在切换之列。
	/// ★ 关卡本身就在放这首时整条逻辑跳过：已经在放，再切等于自己盖自己。
	/// ★ 切回走宿主自己的 `GameStart()`（它内部 `StopBGM` + 放 `flag1` + 起鼓层），
	///   因此原曲**从开头重播**，Godot 侧没有保留原进度可续。
	/// </summary>
	private const string BossBgmTrack = "BrainiacManiac";

	/// <summary>正在压着 BOSS战 BGM 时，我们抽出且仍存活的 BOSS（空了就切回原曲）。</summary>
	private static readonly List<TowerDefenseCharacter> _bossBgmWatch =
		new List<TowerDefenseCharacter>();

	/// <summary>当前是否已经切到 BOSS战（防重复切、并决定要不要切回）。</summary>
	private static bool _bossBgmActive;

	/// <summary>
	/// 本次变身抽出了 BOSS ⇒ 记账并尝试切歌。
	/// 由 <c>RandomImitater.RiExplode</c> 在生成新角色之后调用。
	/// </summary>
	internal static void NotifyBossSpawned(TowerDefenseCharacter boss)
	{
		try
		{
			if (boss == null || !GodotObject.IsInstanceValid(boss))
			{
				return;
			}
			if (!_bossBgmWatch.Contains(boss))
			{
				_bossBgmWatch.Add(boss);
			}
			if (_bossBgmActive)
			{
				return;   // 已经在放了，只补记账
			}
			TowerDefenseBattleFeatureBGM bgm = BossBgmFeature();
			if (bgm == null)
			{
				return;
			}
			// 关卡本来就在放这首 ⇒ 不动，也不登记切回
			TowerDefenseBackgroundMusicConfig cfg = bgm.backgroundMusicConfig;
			if (cfg != null && GodotObject.IsInstanceValid(cfg)
				&& string.Equals(cfg.flag1, BossBgmTrack, StringComparison.Ordinal))
			{
				_bossBgmWatch.Clear();
				return;
			}
			// 与宿主 GameStart 完全同款：MUSIC 通道、起点 0、不随暂停存活
			AudioStreamPlayerMember player = AudioManager.Instance.AudioPlay(
				BossBgmTrack, AudioManagerEnum.TYPE.MUSIC, 0.0, false, false);
			if (player == null || !GodotObject.IsInstanceValid(player))
			{
				return;
			}
			bgm.StopBGM();
			bgm.backgroundAudio = player;
			_bossBgmActive = true;
			LogStatic("抽到 BOSS ⇒ 背景音乐切到「BOSS战」（" + BossBgmTrack + "）。");
		}
		catch
		{
			// 纯表现层：任何失败都不影响战斗。
		}
	}

	/// <summary>
	/// 逐帧收口：我们抽出的 BOSS 全都不在场了 ⇒ 把关卡原曲放回去。
	/// </summary>
	internal static void DriveBossBgm()
	{
		if (!_bossBgmActive)
		{
			return;
		}
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr) || !mgr.IsGameRunning())
			{
				// 战斗已结束（结算 / 失败 / 退出）⇒ 只清状态不放曲，宿主自己会停乐。
				_bossBgmActive = false;
				_bossBgmWatch.Clear();
				return;
			}
			for (int i = _bossBgmWatch.Count - 1; i >= 0; i--)
			{
				TowerDefenseCharacter c = _bossBgmWatch[i];
				if (c == null || !GodotObject.IsInstanceValid(c) || c.die)
				{
					_bossBgmWatch.RemoveAt(i);
				}
			}
			if (_bossBgmWatch.Count > 0)
			{
				return;
			}
			RestoreBossBgm("BOSS 已倒下");
		}
		catch
		{
		}
	}

	/// <summary>
	/// 切回关卡原曲（走宿主 <c>GameStart</c>，原曲从头重播）。
	/// 战斗已经结束（`IsGameRunning` 为假）时只清状态不放曲，免得在结算/失败画面里放歌。
	/// </summary>
	private static void RestoreBossBgm(string reason)
	{
		if (!_bossBgmActive)
		{
			_bossBgmWatch.Clear();
			return;
		}
		_bossBgmActive = false;
		_bossBgmWatch.Clear();
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr) || !mgr.IsGameRunning())
			{
				return;
			}
			TowerDefenseBattleFeatureBGM bgm = BossBgmFeature();
			if (bgm == null)
			{
				return;
			}
			bgm.GameStart();
			LogStatic("恢复关卡原背景音乐（" + reason + "）。");
		}
		catch
		{
		}
	}

	/// <summary>
	/// 取战斗场景里的 BGM feature：`TowerDefenseControlNew.featureDictionary` 是公开的，
	/// 按类型找出那一个（宿主没有公开的 GetBGMFeature）。找到后缓存，失效自动重找。
	/// </summary>
	private static TowerDefenseBattleFeatureBGM BossBgmFeature()
	{
		TowerDefenseManager mgr = TowerDefenseManager.Instance;
		if (mgr == null || !GodotObject.IsInstanceValid(mgr))
		{
			return null;
		}
		TowerDefenseControlNew control = mgr.currentControl;
		if (control == null || !GodotObject.IsInstanceValid(control))
		{
			return null;
		}
		System.Collections.Generic.Dictionary<Godot.StringName, TowerDefenseBattleFeature>
			features = control.featureDictionary;
		if (features == null)
		{
			return null;
		}
		foreach (KeyValuePair<Godot.StringName, TowerDefenseBattleFeature> kv in features)
		{
			TowerDefenseBattleFeatureBGM bgm = kv.Value as TowerDefenseBattleFeatureBGM;
			if (bgm != null && GodotObject.IsInstanceValid(bgm))
			{
				return bgm;
			}
		}
		return null;
	}

	// ================================================================ 种植闸门

	/// <summary>种植扩展点是否接上了（只报一次，见 <c>Initialize</c> 的日志）。</summary>
	private bool _placementGateOk;

	/// <summary>
	/// 接宿主的「种植许可」扩展点：<c>XWModHookPoints.PlantPlacement</c>。
	///
	/// 为什么必须走这一步，光靠 <c>plantGridType</c> 的数据表达不了：
	/// 一次放行要经过 <c>TowerDefenseCellInstance.CanPacketPlant</c>，它最后两道闸门是
	///   · IL_0d1c：本格**任意一层**地形在列表里、且那一层槽位空 ⇒ 放行；
	///   · IL_0d82：<c>GetCharacterWhoHasGridType</c> 只要找到一个「占位者的
	///     plantGridOverrideType 落在自己列表里」的角色 ⇒ **无条件放行**。
	/// 而墓碑(8)/弹坑(9)/花盆(6)/睡莲(5)这些「地形载体」正是 override≠0 的角色。
	/// 于是格上只要站着这么一位载体，它就把判定整体点亮，**完全不看同一格上还
	/// 有没有别的植物** ⇒ 墓碑格上已经种了一株普通植物时，随机模仿者照样落得下去
	/// （叠种）。要放行空墓碑格就得含 8/9，含了 8/9 就拦不住叠种 ⇒ 数据层面无解。
	///
	/// 接法用 <c>After</c>。这个扩展点是宿主专门留给 Mod 的**种植许可闸门**：
	/// <c>ModAllowsPlacement</c> 递给调度器的「原操作」是常量 <c>true</c>
	/// （IL_0051 → <c>&lt;&gt;c.&lt;ModAllowsPlacement&gt;b__112_0</c> = <c>ldc.i4.1; ret</c>），
	/// 也就是说宿主自己的地形判定并不在这里，这里纯粹是「Mod 有没有话要说不」。
	/// 因此 <c>After</c> 拿到的入参是**链上前面其它 Mod 的结论**：
	///   · 不是本 Mod 的卡 ⇒ 立刻原样交回，别的 Mod 的结论一律不改；
	///   · 前面有别的 Mod 否决过 ⇒ 继续是否决（只在 true 时才追加否决 ⇒ 只会更严不会更松）；
	///   · 拿不到 / 抛异常 ⇒ 原样返回 true，退化成今天的行为，不报错不崩。
	/// 地形式样能不能种仍由 <c>CanPacketPlant</c> 在别处把关，这里只补它表达不了的
	/// 「叠种」这一条 ⇒ 其它植物的落点规则一点没变。
	///
	/// ★ 时机正好：这个扩展点在 <c>TowerDefenseInGamePacketShow.Plant</c> 里是
	///   IL_0041 调用，**早于** IL_00A3 扣阳光。否决 ⇒ 阳光不动、卡不进冷却、
	///   不落任何角色，和普通「这格种不下」一模一样。
	/// ★ 变身时开出的植物/僵尸不受影响：那条路走 <c>TowerDefensePacketConfig.Plant</c>，
	///   不经过卡片层，也就不会回调这里。
	/// </summary>
	private void RegisterPlacementGate(XWModRuntimeContext context)
	{
		_placementGateOk = false;
		if (context == null)
		{
			return;
		}
		try
		{
			context.Hooks.After(
				XWModHookPoints.PlantPlacement,
				MyKey + ".placement-gate",
				new Func<XWModPlantPlacementRequest, bool, bool>(BlockStackingPlanting));
			_placementGateOk = true;
		}
		catch (Exception ex)
		{
			// 接不上就退回「按地形数据判定」，功能整体仍可用（只是拦不住叠种）。
			Log("种植扩展点没接上（叠种拦截不生效）：" + ex.Message);
		}
	}

	/// <summary>
	/// 种植许可回调：宿主的结论为 true 时，再检查「这一格是否已站着别的实体植物」。
	/// </summary>
	private static bool BlockStackingPlanting(
		XWModPlantPlacementRequest request, bool hostAllows)
	{
		if (!hostAllows)
		{
			return false;   // 链上前面已有 Mod 否决 ⇒ 原样交回，不改别人的结论
		}
		try
		{
			if (request == null)
			{
				return true;
			}
			// Packet 是卡片（宿主把 this 传进来的），只处理我们自己的那张卡。
			TowerDefenseInGamePacketShow card =
				request.Packet as TowerDefenseInGamePacketShow;
			if (card == null || !GodotObject.IsInstanceValid(card) || !IsMyCard(card))
			{
				return true;
			}
			TowerDefenseCellInstance cell = TowerDefenseManager.GetMapCell(request.Cell);
			if (cell == null || !GodotObject.IsInstanceValid(cell))
			{
				return true;
			}
			return !HasGroundPlantOccupant(cell);
		}
		catch
		{
			// 不许抛：扩展点里抛出会被宿主记成「故障 Mod」并摘掉我们的全部注册。
			return true;
		}
	}

	/// <summary>浮空层（咖啡豆那类不占地面格的植物）。</summary>
	private const TowerDefenseEnum.PLANTGRIDTYPE GridAir = TowerDefenseEnum.PLANTGRIDTYPE.AIR;

	/// <summary>围壳层（南瓜壳那类套在别人外面的植物）。</summary>
	private const TowerDefenseEnum.PLANTGRIDTYPE GridSurround =
		TowerDefenseEnum.PLANTGRIDTYPE.SURROUND;

	/// <summary>「没有自己的地形身份」，即普通植物；≠0 的是花盆/睡莲/墓碑/弹坑这类地形载体。</summary>
	private const TowerDefenseEnum.PLANTGRIDTYPE GridNoone =
		TowerDefenseEnum.PLANTGRIDTYPE.NOONE;

	/// <summary>
	/// 这一格里是否站着「会占住这格的普通植物」。
	///
	/// 排除项与玩家的口径一致：
	///   · <c>plantGridOverrideType != NOONE</c>：花盆(6)/睡莲(5)/墓碑(8)/弹坑(9) 这些
	///     「地形载体」不算植物，格上只有它们时照样能种；
	///   · 地形列表含 AIR(4)：浮空植物（咖啡豆 [4,12]）不占地面格；
	///   · 地形列表含 SURROUND(7)：南瓜壳那类围壳，本来就是套在别人外面的；
	///   · 非 <c>TowerDefensePlantConfig</c>：僵尸、地面物件、道具护盾不参与；
	///   · 正在死亡 / 全息投影 / 已被铲掉的角色不参与（口径照抄 <c>CanPacketPlant</c>）。
	/// 其余任何一株植物 ⇒ 这格已经被占住 ⇒ 拦下叠种。
	/// </summary>
	private static bool HasGroundPlantOccupant(TowerDefenseCellInstance cell)
	{
		List<TowerDefenseCharacter> list = cell.characterList;
		if (list == null)
		{
			return false;
		}
		TowerDefenseCharacter shield = cell.itemShield;
		for (int i = 0; i < list.Count; i++)
		{
			TowerDefenseCharacter o = list[i];
			if (o == null || !GodotObject.IsInstanceValid(o) || o.isDestroy || o.isShovel)
			{
				continue;
			}
			// 道具护盾（口径同私有方法 IsCurrentItemShield：是护盾类或与 itemShield 同一对象）
			if (o is TowerDefenseItemSheild || ReferenceEquals(o, shield))
			{
				continue;
			}
			TowerDefenseCharacterInstance inst = o.instance;
			if (inst == null || !GodotObject.IsInstanceValid(inst)
				|| inst.hologram || inst.die)
			{
				continue;
			}
			TowerDefenseCharacterConfig cfg = o.config;
			if (cfg == null || !GodotObject.IsInstanceValid(cfg))
			{
				continue;
			}
			if (!(cfg is TowerDefensePlantConfig))
			{
				continue;
			}
			if (cfg.plantGridOverrideType != GridNoone)
			{
				continue;   // 地形载体
			}
			Godot.Collections.Array<TowerDefenseEnum.PLANTGRIDTYPE> grid =
				cfg.plantGridType;
			if (grid == null)
			{
				continue;
			}
			if (grid.Contains(GridAir) || grid.Contains(GridSurround))
			{
				continue;
			}
			return true;
		}
		return false;
	}

	/// <summary>
	/// 逐帧把「墓碑系植物必须有墓碑才动」这道闸门关掉。
	///
	/// ── 问题 ──
	/// 1.8.0 起，变身时卡池里的植物会临时按本 Mod 的种植范围落地（为了让各地形
	/// 出植物/出僵尸的比例维持在设定值）。墓碑系植物原本 `plantGridType` 只含
	/// GRAVESTONE(8)、只能种在墓碑上，放宽后就可能落到**没有墓碑**的格子上。
	/// 而 <c>GravebusterComponent.IdleProcessing</c> 每帧的判据是：
	///   IL_0033 读 <c>requireGravestoneTarget</c> → IL_003a 本格找不到墓碑
	///   （<c>ResolveGravestoneTarget</c> 走 <c>cell.FindSlotParent</c>）⇒ **直接 return**
	/// ⇒ 它就永远停在 Idle：不动、不吞、不炸。
	/// 实测反馈的正是「墓碑爆破者」（<c>TowerDefensePlantGravebomber</c>）被抽到
	/// 无墓碑格上之后僵在原地。
	///
	/// ── 做法 ──
	/// <c>requireGravestoneTarget</c> 是公开字段，且**只有** <c>IdleProcessing</c> 一处读它
	/// （写它的只有 <c>.ctor</c> 与 <c>ConfigureOnce</c>，后者取自组件定义）。
	/// 所以把它置 false，闸门即消失：下一帧 IL_0048 就会发出
	/// <c>gravebusterStateEvent</c> 进吞噬状态，动画播完 → <c>Over()</c>
	/// → 宿主自己 <c>Destroy</c> 这株植物并触发 <c>OnOver</c> ⇒ 各株按自己的原生
	/// 收尾走：落地版 <c>cell.Clear</c> + 种 <c>CraterG</c>（砸坑）、钻卡版同款，
	/// 爆破者版 <c>_explodeComponent.Explode()</c> + 清掉 3×3 内所有墓碑。
	/// 吞噬与爆炸**全部是宿主原逻辑**，我们只关掉那一道「没有墓碑就别动」的闸门。
	///
	/// ★ 只放开**已核实安全**的那几株，见 <see cref="IsUnblockableGravebuster"/>。
	///   判据是它的 <c>OnOver</c> 收尾有没有解引用 <c>graveStone</c>：
	///     · 墓碑吞噬者 / 钻卡版：`Over(TowerDefenseGravestone)` 完全不碰参数；
	///     · 墓碑爆破者：只用自己 `gridPos` 扫 3×3，参数不碰；
	///     · 墓碑地雷：转手去种 <c>PlantSunMine</c>，参数不碰。
	///   这四种光关闸门就够，收尾全是宿主原逻辑。
	/// ★ 幽灵吞噬者（<c>TowerDefensePlantGhostbuster</c>）另处理：它的收尾在
	///   IL_0160 **无条件**读 <c>graveStone.instance.hitpoints</c>，前面没有任何
	///   墓碑判空 ⇒ 只关闸门会把「僵住」变成「半截状态 + 未处理异常」（幽灵在
	///   IL_0083 已经先 <c>AddChild</c> 出来了，崩溃点之后整段收尾被跳过）。
	///   所以对它额外做回调替换，见 <see cref="EnsureGhostHook"/>。
	///
	/// ★ 安全性：组件里所有用到 <c>graveStone</c> 的地方都有空值守卫 ——
	///   <c>SetGraveDiscardUpPos</c> IL_000b、<c>ResetGravestoneDiscard</c>、
	///   <c>Over</c> 的 <c>OnOver</c> 调用（IL_0019 先 pop 掉参数、只把它当实参传）、
	///   以及存档/同步的三处 <c>IsInstanceValid</c>。
	///   <c>StartGravebuster</c> 与异步协程根本不读这个字段 ⇒ 不会空引用。
	/// ★ 本格**有**墓碑时行为完全不变：IL_003a 那条 `&&` 本来就短路到同一处。
	/// ★ 只作用于**本 Mod 抽出来的**那几株（按变身前后差集认定），玩家自己种的
	///   不受影响（它本来就种不到没有墓碑的格子上）。
	/// ★ 逐帧重试而不是只改一次：组件的 <c>ConfigureOnce</c> 可能晚于我们这一次
	///   变身收尾（会把字段按定义重新写回 true），改到它不再是 true 为止。
	/// </summary>
	private static readonly List<TowerDefenseCharacter> _gbPending =
		new List<TowerDefenseCharacter>();

	/// <summary>已经报过「放开闸门」的植物；每株只报一次，避免逐帧刷屏。</summary>
	private static readonly HashSet<TowerDefenseCharacter> _gbLogged =
		new HashSet<TowerDefenseCharacter>();

	/// <summary>
	/// 这株是不是「可以安全放开墓碑闸门」的墓碑系植物。
	///
	/// 走白名单，而不是「凡带 <c>GravebusterComponent</c> 就放」：未知植物可能在
	/// 收尾里读墓碑 ⇒ 放开等于把「僵住」换成「抛异常」。白名单漏掉时只会保持
	/// 原生表现（停在原地），不会崩。
	/// ★ <c>TowerDefensePlantGhostbuster</c> 的收尾确实无条件读墓碑，但它同时被
	///   <see cref="EnsureGhostHook"/> 换了回调 ⇒ 在这里也在名单内。
	/// </summary>
	private static bool IsUnblockableGravebuster(TowerDefenseCharacter c)
	{
		return c is TowerDefensePlantGravebuster
			|| c is TowerDefensePlantGravebusterG
			|| c is TowerDefensePlantGravebomber
			|| c is TowerDefensePlantGraveMine
			|| c is TowerDefensePlantGhostbuster;
	}

	// ---------------- 幽灵吞噬者：收尾回调的安全替换 ----------------

	/// <summary>
	/// 摘掉幽灵吞噬者的原生 <c>OnOver</c>，换上 <see cref="HandleGhostOver"/>。
	///
	/// ── 为什么这株不能只关闸门 ──
	/// 原生收尾 <c>TowerDefensePlantGhostbuster.GravebusterOver</c> 是个 async void
	/// 协程，整段里 <c>graveStone</c> 只被读一次，位置在 IL_0154–IL_016f：
	///   <c>zombie.instance.hitpoints = graveStone.instance.hitpoints</c>
	/// 前面**没有任何墓碑判空**（它只有两处 <c>IsInstanceValid</c>，分别守着
	/// <c>zombie</c> 和多人同步用的 <c>currentControl</c>）。而幽灵早在 IL_0083
	/// 就已经 <c>AddChild</c> 上场了 ⇒ 无墓碑时的表现不是「不干活」，而是
	/// 「招出一只幽灵 + 抛未处理异常 + IL_0174 之后整段收尾被跳过」。
	/// 单机关闸门一定会走到这条路径（守卫 IL_002d 只在多人非主机时才提前跳出）。
	///
	/// ── 做法 ──
	/// <c>OnOver</c> 是 C# 事件（<c>add_/remove_OnOver</c> 公开，字段私有），
	/// 用「委托相等」摘掉原生那一个：<c>TowerDefensePlantGhostbuster.GravebusterOver</c>
	/// 是公开实例方法，<c>GravebusterComponent.OverEventHandler</c> 是嵌套公开委托类型 ⇒
	/// 我们 <c>new OverEventHandler(host.GravebusterOver)</c> 构造出来的委托与
	/// <c>host._Ready</c>（IL_002e–IL_0034）注册的那个 <c>Delegate.Equals</c> 为真 ⇒
	/// <c>-=</c> 能精准摘掉它。随后 <c>+=</c> 我们自己的包装，包装里：
	///   · 墓碑**有效** ⇒ 原样调原生 <c>host.GravebusterOver(gs)</c>（一行都不改，
	///     包括「幽灵耐久=墓碑耐久」这条设计）；
	///   · 墓碑**无效** ⇒ 按原生流程自己招一只魅惑幽灵，耐久用幽灵卡的配置默认值。
	/// ★ 顺序要紧：原生的 Invoke 在我们之后挂上，若先 <c>+=</c> 再 <c>-=</c> 不会重排，
	///   但**只 <c>+=</c> 不 <c>-=</c>** 会变成「原生先崩、我们后跑」⇒ 必须先摘再挂。
	/// ★ 只在**非多人**时接管：多人下原生还要发 <c>SendSpawnCharacterAt</c> 同步，
	///   我们复刻不全 ⇒ 联机场景保持原生表现（有墓碑才动，本来就是原生口径）。
	/// ★ 返回值给 <see cref="DriveGravebuster"/>：**只有 true 才允许关闸门**。
	///   联机下我们放弃接管、组件还没挂上也返回 false —— 这两情况下闸门必须留着，
	///   否则原生收尾会跑到那句读墓碑血量的空引用上（比僵住更糟）。
	/// ★ 时机：原生回调是在宿主 <c>_Ready</c> 里注册的，而 <c>_Ready</c> 发生在
	///   <c>AddChild</c> 时（早于本 Mod 的差集扫描不一定成立）⇒ 只在一处摘可能扑空，
	///   所以交给 <see cref="DriveGravebuster"/> 逐帧重试，直到确认摘挂成功。
	/// </summary>
	private static bool EnsureGhostHook(TowerDefenseCharacter c)
	{
		if (_ghostHooked.Contains(c))
		{
			return _ghostHookOk.Contains(c);
		}
		TowerDefensePlantGhostbuster host = c as TowerDefensePlantGhostbuster;
		if (host == null)
		{
			return false;
		}
		try
		{
			if (Global.Instance == null || !GodotObject.IsInstanceValid(Global.Instance)
				|| Global.Instance.isMultiplayerMode)
			{
				// 联机：不接管，闸门也不动 ⇒ 完全原生表现。
				_ghostHooked.Add(c);
				return false;
			}
			GravebusterComponent gb = GravebusterOf(c);
			if (gb == null)
			{
				return false;   // 组件还没挂上 ⇒ 下一帧再来
			}
			GravebusterComponent.OverEventHandler mine
				= new GravebusterComponent.OverEventHandler(
					(TowerDefenseGravestone gs) => HandleGhostOver(host, gs));
			gb.OnOver -= new GravebusterComponent.OverEventHandler(host.GravebusterOver);
			if (_ghostHandlers.ContainsKey(c))
			{
				gb.OnOver -= _ghostHandlers[c];   // 防重复：先摘掉我们自己那一份
			}
			gb.OnOver += mine;
			_ghostHandlers[c] = mine;
			_ghostHooked.Add(c);
			_ghostHookOk.Add(c);
			LogStatic("抽出的幽灵吞噬者落在没有墓碑的格子 ⇒ 已接管它的收尾："
				+ "有墓碑时按原生流程召唤（幽灵耐久=墓碑耐久），"
				+ "没有墓碑时同样吞噬并召唤一只魅惑僵尸幽灵（耐久取幽灵卡默认值）。");
			return true;
		}
		catch
		{
			// 失败就保持原生表现，下一帧还会再试。
			return false;
		}
	}

	/// <summary>
	/// 换上场的收尾包装：墓碑有效 ⇒ 原样转调原生；墓碑无效 ⇒ 自己招幽灵。
	/// ★ 绝不许抛：这里是 <c>GravebusterComponent.Over()</c> 的委托链上，抛出会
	///   打断宿主销毁流程（IL_0027 之后那句 <c>parent.Destroy(true)</c> 就跑不到了）。
	/// ★ <c>over</c> 这个防重入位不能乱设：原生协程开头 IL_0015 就是
	///   <c>if (host.over) return</c> ⇒ 若我们先置 true 再转调原生，原生会**直接跳出**、
	///   幽灵根本招不出来。所以：
	///   · 墓碑有效 ⇒ 不碰 <c>over</c>，交给原生自己去置位（顺带让链上残留的原生
	///     注册被它的自我防重入跳过）；
	///   · 墓碑无效 ⇒ 先置 <c>over = true</c> 再自己召唤，这样万一 <c>-=</c> 时原生
	///     还没注册（<c>_Ready</c> 晚于我们），它随后挂上并跑到时也会被自己的
	///     防重入挡住 ⇒ 不会走到那句读墓碑血量的空引用。
	/// </summary>
	private static void HandleGhostOver(TowerDefensePlantGhostbuster host,
		TowerDefenseGravestone graveStone)
	{
		try
		{
			if (host == null || !GodotObject.IsInstanceValid(host))
			{
				return;
			}
			if (graveStone != null && GodotObject.IsInstanceValid(graveStone)
				&& graveStone.instance != null)
			{
				host.GravebusterOver(graveStone);   // 完全原生，连防重入位都由它自己管
				return;
			}
			if (host.over)
			{
				return;   // 已经召过了
			}
			host.over = true;
			SummonGhostWithoutGravestone(host);
		}
		catch
		{
			// 不许抛。
		}
	}

	/// <summary>
	/// 无墓碑版的召唤：照抄原生协程的公开调用序列
	/// （<c>GetPacketConfig("ZombieGhost")</c> → <c>Create</c> → <c>AddChild</c>
	/// → 下一帧 <c>Walk</c> + 阵营继承），只去掉「读墓碑耐久」那一句。
	/// </summary>
	private static void SummonGhostWithoutGravestone(TowerDefensePlantGhostbuster host)
	{
		TowerDefensePacketConfig cfg = TowerDefenseManager.GetPacketConfig(GhostZombieKey);
		if (cfg == null || !GodotObject.IsInstanceValid(cfg))
		{
			return;
		}
		TowerDefenseCharacter ghost = cfg.Create(host.GetLogicalGlobalPosition(),
			host.gridPos, host.groundHeight, default(EconomyAccountId));
		if (ghost == null || !GodotObject.IsInstanceValid(ghost))
		{
			return;
		}
		Node holder = TowerDefenseGroundItemBase.characterNode;
		if (holder == null || !GodotObject.IsInstanceValid(holder))
		{
			return;
		}
		holder.AddChild(ghost, false, 0);
		// 原生在这里 await 了一个 physics_frame 才走 Walk/阵营 ⇒ 改成逐帧推进，
		// 语义一致（同一物理帧之后），但不引入 async void。
		_ghostLateSetup.Add(new GhostLateSetup(host, ghost));
	}

	/// <summary>召唤之后延一帧的收尾（对应原生协程 await 之后的那半段）。</summary>
	private static void FinishGhostSetup(GhostLateSetup item)
	{
		TowerDefensePlantGhostbuster host = item.Host;
		TowerDefenseCharacter ghost = item.Ghost;
		if (host == null || !GodotObject.IsInstanceValid(host)
			|| ghost == null || !GodotObject.IsInstanceValid(ghost))
		{
			return;
		}
		TowerDefenseZombie zombie = ghost as TowerDefenseZombie;
		if (zombie != null)
		{
			zombie.Walk();
		}
		// 原生 IL_011d–IL_014f 的确切形状（两条分支在 IL_0148 **汇合**）：
		//   · 宿主没用沙盒队伍 且 宿主未被魅惑 ⇒ 先把幽灵 Hypnoses(-1) 转成我方；
		//   · 随后**无条件**执行 host.InheritSandboxTeam(ghost)：receiver 是**宿主**，
		//     语义是「让 ghost 继承 host 的阵营/朝向」（IL_000e 先查 host 自己的
		//     UsesSandboxTeams，为 false 就直接 ret ⇒ 普通关卡这一步是空操作）。
		// ★ 之前写成 else 分支里的 ghost.InheritSandboxTeam(host) 是**双重错误**：
		//   方向反了（变成让植物去继承僵尸阵营），而且沙盒队伍关卡里
		//   第一条分支被跳过 ⇒ 幽灵既没被魅惑也没拿到我方阵营 ⇒ 敌对。
		// ★ UsesSandboxTeams 的 getter 在宿主里是 Family，Mod 程序集编译期访问不到
		//   ⇒ 反射读；读不到按 false 处理（单机普通关卡即此值）。
		bool sandbox = ReadUsesSandboxTeams(host);
		bool hostHypnosed = host.instance != null && host.instance.hypnoses;
		if (!sandbox && !hostHypnosed)
		{
			ghost.Hypnoses(-1.0, true, null);
		}
		host.InheritSandboxTeam(ghost);
		// 到这里就没有「读墓碑耐久」那一句了：耐久保持幽灵卡自己的配置值。
	}

	/// <summary>反射读 <c>TowerDefenseCharacter.UsesSandboxTeams</c>（getter 是 Family）。</summary>
	private static bool ReadUsesSandboxTeams(TowerDefenseCharacter c)
	{
		try
		{
			if (_usesSandboxGetter == null)
			{
				PropertyInfo pi = null;
				for (Type t = c.GetType(); t != null && pi == null; t = t.BaseType)
				{
					pi = t.GetProperty("UsesSandboxTeams",
						BindingFlags.Public | BindingFlags.NonPublic
						| BindingFlags.Instance | BindingFlags.DeclaredOnly);
				}
				_usesSandboxGetter = (pi == null) ? null : pi.GetGetMethod(true);
				if (_usesSandboxGetter == null)
				{
					return false;
				}
			}
			return (bool)_usesSandboxGetter.Invoke(c, null);
		}
		catch
		{
			return false;
		}
	}

	private static MethodInfo _usesSandboxGetter;

	/// <summary>原生收尾里召唤的那只幽灵的卡名（<c>GravebusterOver</c> IL_0045）。</summary>
	private const string GhostZombieKey = "ZombieGhost";

	/// <summary>已确认接管过的植物（含联机下主动放弃的），不再重复动手。</summary>
	private static readonly HashSet<TowerDefenseCharacter> _ghostHooked =
		new HashSet<TowerDefenseCharacter>();

	/// <summary>接管**成功**（回调确实换掉了）的植物 ⇒ 只有它们才允许关墓碑闸门。</summary>
	private static readonly HashSet<TowerDefenseCharacter> _ghostHookOk =
		new HashSet<TowerDefenseCharacter>();

	/// <summary>植物 → 我们挂上去的那个委托（用于幂等：先摘再挂）。</summary>
	private static readonly Dictionary<TowerDefenseCharacter,
		GravebusterComponent.OverEventHandler> _ghostHandlers =
		new Dictionary<TowerDefenseCharacter, GravebusterComponent.OverEventHandler>();

	/// <summary>等一帧收尾的幽灵召唤。</summary>
	private static readonly List<GhostLateSetup> _ghostLateSetup =
		new List<GhostLateSetup>();

	/// <summary><see cref="SummonGhostWithoutGravestone"/> 的延帧参数包。</summary>
	private sealed class GhostLateSetup
	{
		public readonly TowerDefensePlantGhostbuster Host;
		public readonly TowerDefenseCharacter Ghost;

		public GhostLateSetup(TowerDefensePlantGhostbuster host, TowerDefenseCharacter ghost)
		{
			Host = host;
			Ghost = ghost;
		}
	}

	/// <summary>
	/// 一次变身对应一个「延帧产物窗」。
	///
	/// ★ 为什么必须延帧：植物走 <c>TowerDefensePacketConfig.Plant()</c>，里面有
	///   <c>CallDeferred</c>（IL_037b / IL_039B）⇒ 它**下一帧**才进
	///   <c>characterRegistry</c>；而 <c>RiExplode</c> 结尾的当帧差集只能看到
	///   同步注册的僵尸。这既解释了 1.10.1「抽到墓碑系植物也能动」完全没生效，
	///   也解释了「抽出的植物仍然是黑白」——彩色化的差集窗只有 3 帧，
	///   植物注册得比它晚就永远漏登（实测：星卡「流星」等保持黑白）。
	/// ★ 为什么是**一组**窗而不是一个：玩家可以一次带 8 张本卡，同一帧里
	///   多个模仿者先后变身 ⇒ 单份窗会被后一次覆盖，前一次的产物没人认。
	///   每次变身各留一份快照（<c>RiExplode</c> 本来就是各拍各的）。
	/// </summary>
	private sealed class ProductScan
	{
		/// <summary>这次变身前的场上角色快照（与彩色化窗共用，绝不清空）。</summary>
		public HashSet<TowerDefenseCharacter> Before;

		/// <summary>产物应该落在哪一格（= 模仿者自己原来那格，变身时已腾空）。</summary>
		public Vector2I Cell;

		/// <summary>还剩几帧。</summary>
		public int Frames;
	}

	private static readonly List<ProductScan> _prodScans = new List<ProductScan>();

	/// <summary>
	/// 延帧窗的帧数。
	/// ★ 实测「流星」这类带出场动画的植物注册得非常晚（8 帧窗口跑完时角色表里
	///   还没有它）⇒ 窗口要放宽。放宽的误伤风险由
	///   <see cref="RandomImitater.LooksLikeProduct"/> 这道闸门挡住。
	/// </summary>
	private const int GravebusterScanFrames = 120;

	/// <summary>
	/// 变身结束后开一个延帧产物窗（由 <c>RiExplode</c> 调用）。
	/// </summary>
	internal static void BeginGravebusterScan(HashSet<TowerDefenseCharacter> before,
		Vector2I cell)
	{
		if (before == null)
		{
			return;
		}
		_prodScans.Add(new ProductScan
		{
			Before = before,
			Cell = cell,
			Frames = GravebusterScanFrames,
		});
	}

	/// <summary>
	/// 逐帧推进所有 <see cref="_prodScans"/>：把延帧才注册出来的**植物产物**
	/// 登记为「本 Mod 产物」（配色）并做落地适配（墓碑闸门 / BOSS 记账）。
	///
	/// ★ 精确口径：只认「落在变身那一格上的新植物」。模仿者变身前会
	///   <c>Destroy(false)</c> 腾空自己那格，抽出的植物就种回这一格 ⇒ 该格上
	///   新出现的植物必然就是产物，不会误伤玩家同期在别处种的植物
	///   （本 Mod 一贯的口径：只作用于模仿者抽出的那几株）。
	/// ★ 窗内**不提前关窗**：一次变身只出一株，但同一帧可能有多次变身各自开窗，
	///   每个窗都要跑满，否则后面的产物会漏。登记与适配都按对象去重 ⇒ 幂等。
	/// ★ 只丢引用、**绝不 <c>Clear()</c> 快照**：它和彩色化窗
	///   （<c>RiExplode</c> 里同一个 <c>before</c> 传下去）是同一次变身的同一份快照。
	/// </summary>
	private static void DriveGravebusterScan()
	{
		if (_prodScans.Count == 0)
		{
			return;
		}
		TowerDefenseManager mgr = TowerDefenseManager.Instance;
		if (mgr == null || !GodotObject.IsInstanceValid(mgr))
		{
			return;
		}
		TowerDefenseBattleCharacterRegistry reg = mgr.characterRegistry;
		if (reg == null)
		{
			return;
		}
		List<TowerDefenseCharacter> list = reg.GetActiveCharacters();
		if (list == null)
		{
			return;
		}
		for (int w = _prodScans.Count - 1; w >= 0; w--)
		{
			ProductScan scan = _prodScans[w];
			scan.Frames--;
			if (scan.Frames <= 0)
			{
				scan.Before = null;
				_prodScans.RemoveAt(w);
				continue;
			}
			HashSet<TowerDefenseCharacter> before = scan.Before;
			if (before == null)
			{
				_prodScans.RemoveAt(w);
				continue;
			}
			for (int i = 0; i < list.Count; i++)
			{
				TowerDefenseCharacter c = list[i];
				if (c == null || before.Contains(c))
				{
					continue;
				}
				if (!(c is TowerDefensePlant))
				{
					continue;   // 僵尸那一路当帧就能认出，本窗只管植物
				}
				// ★ 判据：窗内新出现的植物，且「像是本次产物」。
				//   窗口从 8 帧放宽到 120 帧（实测「流星」注册得比 8 帧晚得多，
				//   变身之后连一行命中都没有 ⇒ 差集那条识别路对它不成立），
				//   放宽后必须精确化，否则会把玩家在这两秒里自己种的植物误当成产物。
				if (!RandomImitater.LooksLikeProduct(c, scan.Cell))
				{
					continue;
				}
				AdaptProduct(c);
			}
		}
	}

	/// <summary>
	/// 落地适配的统一入口：认出「这是本次变身开出的产物」之后，按类型逐个处理。
	///   · BOSS 三类 ⇒ 记账并切「BOSS战」BGM；
	///   · 墓碑系五株 ⇒ 关掉「必须有墓碑才动」的闸门（幽灵吞噬者另换收尾回调）。
	/// ★ 调用点有两处：<see cref="ScanNewProducts"/>（当帧，管僵尸那一路）与
	///   <see cref="DriveGravebusterScan"/>（延帧，管植物那一路）。
	/// </summary>
	internal static void AdaptProduct(TowerDefenseCharacter c)
	{
		if (c == null)
		{
			return;
		}
		// ★ 这里只做「必须精确认出产物」的两类落地适配（墓碑闸门 / BOSS 记账）。
		//   配色不在这里：它改成全局灰阶清扫，不依赖身份识别（见 SweepGrayOnField）。
		if (c is TowerDefenseZombieBoss
			|| c is TowerDefenseZombieBossDave
			|| c is TowerDefenseZombieBossEdgarII)
		{
			NotifyBossSpawned(c);
		}
		if (c is TowerDefensePlantGravebuster
			|| c is TowerDefensePlantGravebusterG
			|| c is TowerDefensePlantGravebomber
			|| c is TowerDefensePlantGraveMine
			|| c is TowerDefensePlantGhostbuster)
		{
			NotifyGravebusterSpawned(c);
		}
	}

	/// <summary>
	/// 清掉本小节所有静态追踪表（卸载/换关时调用）。
	/// ★ 这些表按角色对象引用做键，不清会把上一局的已释放角色一直留在托管堆上。
	/// </summary>
	internal static void ClearGravebusterTracking()
	{
		_gbPending.Clear();
		_gbLogged.Clear();
		_ghostHooked.Clear();
		_ghostHookOk.Clear();
		_ghostHandlers.Clear();
		_ghostLateSetup.Clear();
		_prodScans.Clear();
	}

	/// <summary>变身抽出墓碑系植物 ⇒ 记进待处理表，交给 <see cref="DriveGravebuster"/>。</summary>
	internal static void NotifyGravebusterSpawned(TowerDefenseCharacter plant)
	{
		try
		{
			if (plant == null || !GodotObject.IsInstanceValid(plant))
			{
				return;
			}
			if (!IsUnblockableGravebuster(plant))
			{
				return;
			}
			if (!_gbPending.Contains(plant))
			{
				_gbPending.Add(plant);
			}
			// 幽灵吞噬者的收尾替换不在这里做：原生回调是宿主 _Ready（AddChild 时）
			// 注册的，此刻可能还没注册 ⇒ 由 DriveGravebuster 逐帧确认后再动手。
		}
		catch
		{
		}
	}

	/// <summary>
	/// 取某株角色身上的 <c>GravebusterComponent</c>。
	///
	/// 走宿主自己的泛型取组件入口 <c>ComponentManager.GetComponent&lt;T&gt;()</c>
	/// （<c>TowerDefensePlantGravebusterG._Ready</c> IL_0015 用的就是它）。
	/// ★ 注意 <c>GravebusterComponent</c> **不是** GodotObject（组件运行时是普通类），
	///   所以对它只能用 null 判断，不能调 <c>GodotObject.IsInstanceValid</c>；
	///   生命周期跟着宿主角色走，角色失效时我们就不再访问它。
	/// </summary>
	private static GravebusterComponent GravebusterOf(TowerDefenseCharacter c)
	{
		ComponentManager cm = c.componentManager;
		if (cm == null || !GodotObject.IsInstanceValid(cm))
		{
			return null;
		}
		try
		{
			return cm.GetComponent<GravebusterComponent>();
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// 逐帧消化 <see cref="_gbPending"/>：组件挂上了就把闸门按住（true 时清一次），
	/// 并对幽灵吞噬者确认收尾替换（<see cref="EnsureGhostHook"/>）已经做上。
	///
	/// ★ 为什么不能「清成功一次就出表」：组件的 <c>ConfigureOnce</c> 在
	///   <c>OnOwnerGameplayActivated</c> 里跑（<c>_Ready</c> 之后），会把
	///   <c>requireGravestoneTarget</c> 按组件定义**重新写回 true** ⇒ 只清一次会被盖掉。
	///   所以这株植物活着期间每帧都按住它。
	/// ★ 对有墓碑的场合无副作用：本格有墓碑时 <c>graveStone</c> 有效，
	///   IL_003a 那条 `&&` 本来就走通，闸门开不开结果一样 ⇒ 吞噬行为完全不变。
	/// ★ 角色一离场（吞完引爆 / 被铲 / 死亡）即出表并丢掉委托引用，不残留。
	/// </summary>
	internal static void DriveGravebuster()
	{
		// ⓪ 延帧差集：把「下一帧才注册出来」的植物产物认出来（见 BeginGravebusterScan）。
		DriveGravebusterScan();

		// ① 幽灵召唤的延帧收尾（对应原生协程 await physics_frame 之后的那半段）。
		if (_ghostLateSetup.Count > 0)
		{
			try
			{
				for (int i = _ghostLateSetup.Count - 1; i >= 0; i--)
				{
					GhostLateSetup item = _ghostLateSetup[i];
					_ghostLateSetup.RemoveAt(i);
					FinishGhostSetup(item);
				}
			}
			catch
			{
				// 纯兜底：失败只影响这一只幽灵的阵营收尾，不影响战斗。
			}
		}

		// ② 闸门 + 幽灵收尾接管。
		if (_gbPending.Count == 0)
		{
			return;
		}
		try
		{
			for (int i = _gbPending.Count - 1; i >= 0; i--)
			{
				TowerDefenseCharacter c = _gbPending[i];
				if (c == null || !GodotObject.IsInstanceValid(c) || c.die)
				{
					_gbPending.RemoveAt(i);
					_gbLogged.Remove(c);
					_ghostHooked.Remove(c);
					_ghostHookOk.Remove(c);
					if (_ghostHandlers.ContainsKey(c))
					{
						// 委托链上的那一份随组件一起消失，这里只清我们的引用，
						// 不再 -=（宿主 _ExitTree 会对相等委托做一次，重复摘是无害空操作）。
						_ghostHandlers.Remove(c);
					}
					continue;
				}
				// 幽灵吞噬者：收尾替换**成功**才允许关闸门。没换掉回调就关闸门，
				// 原生收尾会跑到那句读墓碑血量的空引用上 ⇒ 比僵住更糟。
				bool ok = !(c is TowerDefensePlantGhostbuster) || EnsureGhostHook(c);
				if (!ok)
				{
					continue;
				}
				GravebusterComponent gb = GravebusterOf(c);
				if (gb != null && gb.requireGravestoneTarget)
				{
					gb.requireGravestoneTarget = false;
					if (_gbLogged.Add(c))
					{
						LogStatic("抽出的墓碑系植物落在没有墓碑的格子 ⇒ 已放开"
							+ "「必须有墓碑才动」的闸门，按原生流程吞噬并引爆。");
					}
				}
			}
		}
		catch
		{
			// 纯兜底：失败就保持原生表现（停在原地），不影响战斗。
		}
	}

	// ================================================================ 种植残影

	/// <summary>
	/// 逐帧抹掉「其实种不下去」的可种植残影。
	///
	/// 落点上那层半透明残影由 <c>PacketPickControl.ProcessPacketPickCore</c> 决定：
	/// 它只问 <c>TowerDefenseCellInstance.CanPacketPlant</c>（IL_0557），**不会**经过
	/// <c>XWModHookPoints.PlantPlacement</c> 那个 Mod 扩展点 ⇒ 接在那儿的叠种拦截
	/// 拦得住落地、拦不住残影，于是出现「影子上得去、真身种不上」的误导画面。
	///
	/// 宿主另有一条 <c>XWModPlantPlacementService.Reserve/Release</c> 可让
	/// <c>CanPacketPlant</c> 直接否决，但走不通：一是这几个成员在宿主程序集里不是
	/// public，二是预留**整格**生效，会把南瓜壳套植物这类本该正常的落点一起堵死。
	///
	/// 所以按宿主自己的节奏补一刀：残影每帧重画（同一次调用开头 IL_00c8 先把
	/// <c>plantSpriteList</c> 逐个设不可见，通过校验后 IL_0799 再把第 0 个设可见），
	/// 我们就在每帧末尾用**同一个判据**（<see cref="HasGroundPlantOccupant"/>，
	/// 与 <see cref="BlockStackingPlanting"/> 完全一致）把残影关掉 ⇒ 影子和落地
	/// 许可始终同步：别处该有影子的地方一个不少，拦下的那一格一个不多。
	///
	/// ★ 只在扩展点确实接上时才做：接不上就没有拦截，那格是真能种的，藏影子反而是错的。
	/// ★ 只碰本卡的残影，别的卡与别的 Mod 的落点预览一概不动。
	/// </summary>
	private void DrivePlacementGhost()
	{
		if (!_placementGateOk)
		{
			return;
		}
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			PacketPickControl ppc = mgr.GetPacketPickControl();
			if (ppc == null || !GodotObject.IsInstanceValid(ppc))
			{
				return;
			}
			// 拿到「本卡 + 光标所指那一格」；返回 false 表示这帧没什么可管的。
			if (!HoverCellHoldsGroundPlant(mgr, ppc))
			{
				return;
			}
			List<AdobeAnimateSprite> sprites = ppc.plantSpriteList;
			if (sprites == null)
			{
				return;
			}
			for (int i = 0; i < sprites.Count; i++)
			{
				AdobeAnimateSprite sprite = sprites[i];
				if (sprite != null && GodotObject.IsInstanceValid(sprite))
				{
					sprite.Visible = false;
				}
			}
		}
		catch
		{
			// 每帧逻辑：静默跳过，不影响种植本身。
		}
	}

	/// <summary>
	/// 本卡持在手上时，光标指着的那一格上是否有会被拦截的实体植物。
	/// 返回 false 表示「这帧不用管残影」：没持卡、不在选卡状态、指针不在地图上、
	/// 或那一格本来就能种。
	///
	/// 取格口径照抄宿主 <c>TowerDefenseBattleFeatureMap.ProcessInput</c>：
	/// 视口鼠标坐标 → 画布逆变换成世界坐标（IL_00e7-IL_00f1）→ 世界坐标换格号
	/// （IL_00fb）→ 格号取格子（IL_0104）。宿主那份带一帧缓存，我们每帧自己算，
	/// 结果一致且不受缓存时机影响。
	/// </summary>
	private static bool HoverCellHoldsGroundPlant(
		TowerDefenseManager mgr, PacketPickControl ppc)
	{
		if (!mgr.IsGameRunning())
		{
			return false;
		}
		TowerDefenseInGamePacketShow card = ppc.packetPick;
		if (card == null || !GodotObject.IsInstanceValid(card) || !IsMyCard(card))
		{
			return false;
		}
		TowerDefenseMapControl mapControl = ppc.mapControl;
		if (mapControl == null || !GodotObject.IsInstanceValid(mapControl))
		{
			return false;
		}
		Godot.Viewport viewport = mapControl.GetViewport();
		if (viewport == null || !GodotObject.IsInstanceValid(viewport))
		{
			return false;
		}
		Vector2 world = mapControl.GetCanvasTransform().AffineInverse()
			* viewport.GetMousePosition();
		Vector2I cellPos = mgr.GetMapGridPosFromMouse(world);
		if (!mgr.CheckMapGridPosIn(cellPos))
		{
			return false;
		}
		TowerDefenseCellInstance cell = TowerDefenseManager.GetMapCell(cellPos);
		if (cell == null || !GodotObject.IsInstanceValid(cell))
		{
			return false;
		}
		return HasGroundPlantOccupant(cell);
	}

	// ================================================================ 每帧

	/// <summary>
	/// 只做一件事：等 `ResourceManager` 就绪后注册一次混合卡池，然后不再做任何事。
	/// （不在 `Initialize` 里直接注册，因为那时 ResourceManager 可能还没起来。）
	/// </summary>
	private void OnFrame()
	{
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree))
			{
				return;
			}
			// ★ 设置随时可能被玩家改（游戏内「Mod 管理 → 配置」保存后立刻生效）。
			//   放在 EnsureMixedBank 之前：若本帧发现设置变了，先把卡池标记为待重建，
			//   紧接着的 EnsureMixedBank 就会用新参数重建，同一帧完成，无需等下一帧。
			DriveSettingsWatch();
			if (!_bankOk)
			{
				_bankOk = EnsureMixedBank();
			}
			// 图鉴概率文字 / 阳光消耗待同步：Initialize 阶段资源可能还没加载，那时改不动；
			// 加载完成后由这里补上，成功一次即停，不会每帧反复写资源。
			if (_presentationDirty
				&& SyncCardPresentation(_rawPlantChance, _rawSunCost))
			{
				_presentationDirty = false;
			}
			if (InfiniteSelect)
			{
				DriveInfiniteSelect();
			}
			if (ReselectTakeover)
			{
				DriveReselectHook();
				// 按下后盯几帧：万一还有一条原生 Godot 连线把结果覆盖掉，就重建回来。
				DriveReselectReconcile();
			}
			if (GroupTakeover)
			{
				DriveGroupHook();
			}
			// 把本卡追加进「钻卡」页签的分类表（详见 DriveDiamondTab 注释）。
			DriveDiamondTab();
			// 把本卡追加进**自制关卡编辑器**的卡牌库（它读的是全局总卡池 "Total"，
			// 与关卡选卡界面那份不是同一个对象，详见 DriveEditorCardBank 注释）。
			DriveEditorCardBank();
			// 右键手动放行小推车（内部自带「游戏进行中 + 点中推车」双重门槛）。
			DriveMowerRightClick();
			// 场上有模仿者时，逐帧把「免咬」开关按回可被咬的取值
			// （引擎进旋转状态会自己设无敌/免咬，只在 _Ready 写一次会被覆写）
			RandomImitater.TickAllBiteable();
			// 逐帧清掉游戏给「本 Mod 抽出」的产物加的黑白滤镜（彩色化开关为开时）。
			// 必须逐帧做：植物那条路径是 CallDeferred，会晚于我们设置 ⇒ 只设一次会被盖掉。
			RandomImitater.TickAllColorize();
			// ★ 全局灰阶清扫：不依赖「认出产物」，直接把场上除模仿者本体外所有带灰的
			//   角色恢复原色。实测「流星」注册得极晚（延帧窗放到 120 帧仍取不到它）
			//   ⇒ 差集那条识别路对它永远不成立，只能靠这一条兜住（见 SweepGrayOnField）。
			RandomImitater.SweepGrayOnField();
			// 抹掉「其实种不下去」的可种植残影（必须逐帧：影子每帧由宿主重画）。
			DrivePlacementGhost();
			// 抽出的 BOSS 全倒下后，把关卡原曲放回去。
			DriveBossBgm();
			// 抽出的墓碑吞噬者落在无墓碑格时，按住「必须有墓碑才动」的闸门。
			DriveGravebuster();
		}
		catch (Exception ex)
		{
			if (_diag < 200)
			{
				_diag = 200;
				Log("每帧驱动异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	// ================================================================ 无限选取

	/// <summary>
	/// 每帧找出选卡界面里「本卡的池卡」，接管它的 OnPressed 并保持可点。
	///
	/// ★ 不用 packetList 记账，而是**直接递归扫 packetBank 的节点子树**找
	///   `TowerDefenseInGamePacketShow`。原因：卡是**池化/虚拟化**的
	///   （`BindVirtualizedPacket` / `RefreshVirtualizedPacketBindings`），
	///   同一个实例会被反复回收再绑定到不同的 config 上；只有按节点实况扫描才可靠。
	/// </summary>
	private void DriveInfiniteSelect()
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			TowerDefenseBattleFeaturePacketBank feature = mgr.GetPacketBankFeature();
			if (feature == null || !GodotObject.IsInstanceValid(feature))
			{
				return;
			}
			object bankObj = GetMember(feature, "packetBank");
			Node bank = bankObj as Node;
			if (bank == null || !GodotObject.IsInstanceValid(bank))
			{
				return;
			}

			_cardsSeen = 0;
			_mineSeen = 0;
			ScanNodeTree(bank, 0);
		}
		catch (Exception ex)
		{
			if (_diag < 201)
			{
				_diag = 201;
				Log("无限选取驱动异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>递归扫子树里的卡片节点。</summary>
	private void ScanNodeTree(Node node, int depth)
	{
		if (node == null || !GodotObject.IsInstanceValid(node) || depth > 40)
		{
			return;
		}
		try
		{
			TowerDefenseInGamePacketShow card = node as TowerDefenseInGamePacketShow;
			if (card != null)
			{
				_cardsSeen++;
				if (IsMyCard(card))
				{
					_mineSeen++;
					_myPoolCard = card;   // 记一张本卡，供「重选补齐」取动画起点
					HookPoolCard(card);
				}
			}
			int n = node.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				ScanNodeTree(node.GetChild(i), depth + 1);
			}
		}
		catch { }
	}

	/// <summary>这张卡是不是「随机模仿者」（按 originalSaveKey / config.saveKey 判定）。</summary>
	private static bool IsMyCard(TowerDefenseInGamePacketShow card)
	{
		return string.Equals(KeyOf(card), MyKey, StringComparison.Ordinal);
	}

	/// <summary>取一张卡的身份 key（口径与游戏一致：originalSaveKey 非空优先）。</summary>
	private static string KeyOf(TowerDefenseInGamePacketShow card)
	{
		try
		{
			string key = null;
			try { key = card.originalSaveKey; } catch { }
			if (string.IsNullOrEmpty(key))
			{
				TowerDefensePacketConfig c = card.config;
				if (c != null && GodotObject.IsInstanceValid(c))
				{
					key = c.saveKey;
				}
			}
			return key ?? "";
		}
		catch
		{
			return "";
		}
	}

	/// <summary>接管一张卡池卡：换掉 OnPressed，并让它永远保持可点。</summary>
	private void HookPoolCard(TowerDefenseInGamePacketShow card)
	{
		// ① 强制保持可点：alive=false 会让 Pressed() 直接 return
		try { card.alive = true; } catch { }
		try { card.allowPressWhenUnavailable = true; } catch { }
		try { card.enforceRuntimeAvailabilityOnPress = false; } catch { }
		try { SetMember(card, "lock", false); } catch { }

		// ② 检查 OnPressed 现在挂的是不是我们的处理器。
		//    ★ 不能只靠实例 ID 记账：卡是池化/虚拟化的，同一个实例会被回收再绑定到
		//      别的 config 上，游戏也会重新挂上它自己的 PacketChoose 处理器。
		//      所以每次都读活委托来判断。
		try
		{
			object cur = GetEventField(card, "OnPressed");
			if (IsMine(cur))
			{
				return;   // 已经是我们接管的
			}

			MethodInfo handler = typeof(RandomImitaterEntry).GetMethod(
				nameof(OnMyPoolCardPressed),
				BindingFlags.NonPublic | BindingFlags.Instance);
			if (!ReplaceEventField(card, "OnPressed", handler))
			{
				card.OnPressed += OnMyPoolCardPressed;
			}
			if (!_hookLogged)
			{
				_hookLogged = true;
				Log("已接管池卡点击事件（无限选取）；本帧卡片节点 " + _cardsSeen
					+ " 个、本卡 " + _mineSeen + " 个。");
			}
		}
		catch (Exception ex)
		{
			if (!_hookErrLogged)
			{
				_hookErrLogged = true;
				Log("接管池卡点击失败（已吞）：" + ex.Message);
			}
		}
	}

	/// <summary>判断某个委托（可能是多播）里是否挂着我们指定的处理器。</summary>
	private bool IsMine(object del)
	{
		return IsMine(del, nameof(OnMyPoolCardPressed));
	}

	private bool IsMine(object del, string handlerName)
	{
		try
		{
			Delegate d = del as Delegate;
			if (d == null)
			{
				return false;
			}
			foreach (Delegate one in d.GetInvocationList())
			{
				if (one.Target == this && one.Method.Name == handlerName)
				{
					return true;
				}
			}
		}
		catch { }
		return false;
	}

	/// <summary>本卡被点：每次新增一张到卡槽（带选卡飞行动画），直到卡槽满。</summary>
	private void OnMyPoolCardPressed(TowerDefenseInGamePacketShow card)
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			TowerDefenseInGameSeedBank seedBank = mgr.GetSeedBank();
			if (seedBank == null || !GodotObject.IsInstanceValid(seedBank))
			{
				return;
			}
			if (!seedBank.CanAddPacket())
			{
				return;   // 卡槽已满
			}
			TowerDefensePacketConfig cfg = card.config;
			if (cfg == null || !GodotObject.IsInstanceValid(cfg))
			{
				return;
			}

			// ★ 走游戏的 CreateAnime（= PacketChoose 里加卡那一步的原生调用）：
			//   它会「造一张飞行动画卡 + 调 seedBank.AddPacket」，动画与入槽语义都对。
			//   直接调 AddPacket 的话卡片会凭空出现，没有飞入卡槽的动画。
			TowerDefenseBattleFeaturePacketBank feature = mgr.GetPacketBankFeature();
			TowerDefenseInGamePacketBank bank = (feature != null && GodotObject.IsInstanceValid(feature))
				? GetMember(feature, "packetBank") as TowerDefenseInGamePacketBank
				: null;
			if (bank != null && GodotObject.IsInstanceValid(bank))
			{
				Vector2 from = bank.GetCameraPos() + card.GlobalPosition;
				bank.CreateAnime(cfg, from);
			}
			else
			{
				// 兜底：拿不到 packetBank 时退化为直接入槽（无动画）
				seedBank.AddPacket(cfg, false);
			}

			// 池卡这张继续保持可点（原生会把 alive 置 false 变灰）
			card.alive = true;
		}
		catch (Exception ex)
		{
			if (_diag < 202)
			{
				_diag = 202;
				Log("新增卡槽条目异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	// ================================================================ 重选记忆（接管按钮）

	/// <summary>
	/// 每帧看一眼选卡界面的「重选上次卡牌」按钮，把它的 `OnPressed` **整体替换**成
	/// 我们的处理器（原生委托存进 `_reselectOriginal` 作兜底）。
	///
	/// ★ 为什么要替换而不是追加：追加只能在原生恢复完之后「补差额」，而 `AddPacket`
	///   只会追加到 `packetList` 末尾 ⇒ 玩家「先选本卡、后选灰烬」时顺序必然错；
	///   更要命的是原生 `PacketListChoose` 用按 saveKey 去重的 `packetNameSet` 判「已有」，
	///   重复的本卡**最多只能恢复 1 张**（见 `OnReSelectPressed` 的注释）。
	///   替换后由我们自己按存档顺序逐张重建，份数与顺序才都对得上。
	/// ★ 只替换**这一个按钮**，且处理器内部先判断「存档里本卡是否 ≥2 张」：
	///   不满足就原样调用 `_reselectOriginal`，其它卡与普通玩家的体验完全不变。
	/// </summary>
	private void DriveReselectHook()
	{
		// ★ 刻意**不**用「本卡是否在卡池里可见」当门（早期版本用了 `_mineSeen == 0`）。
		//
		//   原因：卡池是**虚拟化**的（`BindVirtualizedPacket` / `_visiblePackets`），
		//   本卡一旦被滚出可视区、或玩家切到了别的分类页，`_mineSeen` 就是 0。
		//   而「重选上次卡牌」是**全局**按钮，跟本卡此刻是否显示在列表里毫无关系。
		//   用 `_mineSeen` 当门 ⇒ 那种时刻我们根本没接管按钮 ⇒ 玩家一按就走原生，
		//   原生把重复的本卡塌成 1 张。这正是「第一次点只补一张」的另一种成因。
		//   代价只是每帧几次反射（`FindReselectButton` + 读一次委托字段），可接受。
		//
		// ★ 而且必须**每帧**继续盯着这个字段：游戏可能在 `GameInit` /
		//   `GameInitFromProgress` 之后再次调用 `_ConnectPacketBankSignals`，
		//   而 `add_OnPressed` 是 `Delegate.Combine` ⇒ 字段会变成 `[我们, 原生]`。
		//   那一帧若玩家正好按下，就是「我们先跑、原生紧接着再跑」，
		//   原生会 `DeleteAllPacket()` + 开关式 `PacketListChoose()` 把重复的本卡塌成 1 张。
		//   每帧收敛成「只有我们」把窗口压到最小，配合按下后的核对彻底消除。
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			TowerDefenseBattleFeaturePacketBank feature = mgr.GetPacketBankFeature();
			if (feature == null || !GodotObject.IsInstanceValid(feature))
			{
				return;   // 不在选卡界面
			}
			Node btn = FindReselectButton(feature);
			if (btn == null || !GodotObject.IsInstanceValid(btn))
			{
				return;
			}
			// ★ 每次读**活委托**判断，不按实例 ID 记账：
			//   游戏在 `_ConnectPacketBankSignals` 里可能重建按钮并重新挂处理器（换场景 / 换分类），
			//   我们的处理器会随之丢掉 —— 记账式会漏挂，读活委托能自愈。
			//
			// ★ 判「是否已接管」用**精确等于**而不是「包含」：
			//   若游戏之后又把自己的处理器 Combine 进来，字段会变成 `[我们, 原生]`，
			//   只判「包含我们」会误认为已接管并直接 return，于是按下时两个都跑 ⇒ 结果退回原生。
			//   这里每次都把字段收敛成「只有我们」，并把原生那部分留底。
			Delegate curD = GetEventField(btn, "OnPressed") as Delegate;
			MethodInfo handler = typeof(RandomImitaterEntry).GetMethod(
				nameof(OnReSelectPressed),
				BindingFlags.NonPublic | BindingFlags.Instance);
			if (handler == null)
			{
				return;
			}
			if (curD != null)
			{
				Delegate[] inv = curD.GetInvocationList();
				if (inv.Length == 1 && inv[0].Target == this
					&& inv[0].Method.Name == nameof(OnReSelectPressed))
				{
					return;   // 已经是「只有我们」的状态
				}
			}
			// 从当前委托里剥掉我们的处理器，剩下的（原生的）留作兜底。
			_reselectOriginal = StripMine(curD, nameof(OnReSelectPressed));
			if (!ReplaceEventField(btn, "OnPressed", handler))
			{
				return;
			}
			if (!_reselectTakeoverLogged)
			{
				_reselectTakeoverLogged = true;
				Log("已接管「重选上次卡牌」按钮（多张本卡按原顺序精确恢复）。");
			}
		}
		catch (Exception ex)
		{
			if (!_reselectErrLogged)
			{
				_reselectErrLogged = true;
				Log("接管「重选」按钮失败（已吞）：" + ex.Message);
			}
		}
	}

	/// <summary>
	/// 找「重选上次卡牌」按钮：与游戏自己的取法一致 ——
	/// `_ConnectPacketBankSignals` IL_0131-IL_0146：
	///   this.packetBank.translate.GetNode("ReSelectButton") as NinePatchButtonBase
	/// </summary>
	private static Node FindReselectButton(TowerDefenseBattleFeaturePacketBank feature)
	{
		try
		{
			object bank = GetMember(feature, "packetBank");
			if (bank == null || !GodotObject.IsInstanceValid((GodotObject)bank))
			{
				return null;
			}
			object translate = GetMember(bank, "translate");
			Control tr = translate as Control;
			if (tr == null || !GodotObject.IsInstanceValid(tr))
			{
				return null;
			}
			return tr.GetNodeOrNull("ReSelectButton");
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// 「重选」被按下：**我们自己**完成整次恢复。
	///
	/// 判据与收窄：只有存档里本卡 ≥2 张时才走自定义路径；否则原样调用原生处理器。
	/// 任何异常都退回原生处理器 ⇒ 最坏情况 = 原生行为，按钮不会失灵。
	///
	/// ★ 为什么必须由我们**整次重建**，而不是让原生跑完再补差额：
	///   原生 `ReSelectButtonPressed` = `DeleteAllPacket()` + `PacketListChoose()`，
	///   而 `PacketListChoose` 对每一项都先问 `seedBank.HasPacket(key)`，
	///   是「已有就跳过」。`HasPacket` 查的是 `packetNameSet` —— 一个**按 saveKey
	///   去重的字典**（`Dictionary&lt;Variant,bool&gt;`），不是按卡张数。
	///   于是存档里 5 张本卡：第 1 张加进去后 `packetNameSet[MyKey] = true`，
	///   剩下 4 张全被判为「已有」跳过 ⇒ **永远只恢复 1 张**。
	///   这就是「第一次点只能补一张」的根因，补差额救不了，只能自己重建。
	/// </summary>
	private void OnReSelectPressed()
	{
		try
		{
			Godot.Collections.Array saved = ReadReselectSave();
			int mine = 0;
			if (saved != null)
			{
				foreach (Variant v in saved)
				{
					if (string.Equals(v.AsString(), MyKey, StringComparison.Ordinal))
					{
						mine++;
					}
				}
			}
			if (mine <= 1)
			{
				// 本卡只有 0~1 张：原生的恢复结果就是对的，完全交给原生。
				CallOriginalReselect();
				return;
			}

			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				CallOriginalReselect();
				return;
			}

			// 期望状态 = 存档里所有「配置仍有效」的 key，**保持存档顺序**。
			// （存档里可能有过期条目 —— 卡池变了 / 卡被删了 —— 取不到 config 就跳过，
			//   否则重建时会中断。）
			List<string> desired = new List<string>();
			if (saved != null)
			{
				foreach (Variant v in saved)
				{
					string key = v.AsString();
					if (string.IsNullOrEmpty(key))
					{
						continue;
					}
					TowerDefensePacketConfig cfg = null;
					try { cfg = TowerDefenseManager.GetPacketConfig(key); } catch { }
					if (cfg == null || !GodotObject.IsInstanceValid(cfg))
					{
						continue;
					}
					desired.Add(key);
				}
			}
			if (desired.Count == 0)
			{
				CallOriginalReselect();
				return;
			}

			int gotMine = ApplyReselect(mgr, desired);

			// 校验：本卡份数没恢复到位就说明这条路径不可靠 ⇒ 回退原生，保证「至少能用」。
			if (gotMine < mine)
			{
				Log("「重选」接管结果不符（本卡 " + gotMine + "/" + mine + "），回退原生处理器。");
				CallOriginalReselect();
				return;
			}

			// ★ 按下之后仍可能被**另一条**原生路径覆盖：
			//   `ReSelectButtonPressed` 不只挂在 C# 的 `OnPressed` 事件上，还被 Godot
			//   当成方法名暴露（`GetGodotMethodList` / `InvokeGodotClassMethod`），
			//   场景里可能另有一条 `pressed → ReSelectButtonPressed` 的连线。
			//   那条连线替换字段是拦不住的，它跑完会 `DeleteAllPacket()` +
			//   开关式的 `PacketListChoose()`，把重复的本卡塌成 1 张。
			//   所以按下后盯几帧，一旦发现卡槽与期望不符就按存档顺序重建。
			_reselectDesired = desired;
			_reselectDesiredMine = mine;
			_reselectWatch = 6;
			_reselectAttempts = 0;

			if (!_reselectLogged)
			{
				_reselectLogged = true;
				Log("「重选」接管生效：存档 " + desired.Count + " 项 → 入槽，其中本卡 "
					+ gotMine + " 张（存档记忆 " + mine + " 张），顺序与存档一致。");
			}
		}
		catch (Exception ex)
		{
			if (!_reselectErrLogged)
			{
				_reselectErrLogged = true;
				Log("「重选」接管异常，回退原生处理器（本条只报一次）：" + ex.Message);
			}
			CallOriginalReselect();
		}
	}

	/// <summary>
	/// 按 <paramref name="keys"/> 的顺序**清空并重建**卡槽，返回重建后卡槽里本卡的张数。
	///
	/// ★ 每张都走游戏的 `TowerDefenseInGamePacketBank.CreateAnime`（= 原生 `PacketChoose`
	///   加卡那一步调用的同一个方法），而不是自己 `AddPacket`，好处有两个：
	///     · 它是**唯一**能给出飞入动画的入口，顺带把动画要了回来（之前直接 `AddPacket`
	///       是凭空出现）；
	///     · 它会自己调 `seedBank.AddPacket(cfg, IsGameRunning)`，卡片的初始化语义
	///       与原生完全一致（选卡界面下 `IsGameRunning` 为 false ⇒ 走「可取消」那套）。
	///   `CreateAnime` 对 `IsInsideTree` / `cfg` / `seedBank` / `animeNode` 都有前置校验，
	///   校验不过会**静默 return 且不加卡**，所以下面用 `packetList.Count` 有没有涨来判断，
	///   没涨就退化为直接入槽（自己补 `Visible`，因为少了 Tween 回调那一步）。
	/// </summary>
	private int ApplyReselect(TowerDefenseManager mgr, List<string> keys)
	{
		TowerDefenseInGameSeedBank seedBank = mgr.GetSeedBank();
		if (seedBank == null || !GodotObject.IsInstanceValid(seedBank))
		{
			return 0;
		}

		// 动画起点：与 `OnMyPoolCardPressed` 一致 —— 从池子里那张本卡的位置飞出。
		// 拿不到那张卡时退回原生 `PacketListChoose` 用的「相机位置 + (300, 260)」。
		TowerDefenseBattleFeaturePacketBank feature = mgr.GetPacketBankFeature();
		TowerDefenseInGamePacketBank bank = (feature != null && GodotObject.IsInstanceValid(feature))
			? GetMember(feature, "packetBank") as TowerDefenseInGamePacketBank
			: null;
		bool useAnime = bank != null && GodotObject.IsInstanceValid(bank);
		Vector2 from = Vector2.Zero;
		if (useAnime)
		{
			try
			{
				Vector2 camera = bank.GetCameraPos();
				TowerDefenseInGamePacketShow src = _myPoolCard;
				from = (src != null && GodotObject.IsInstanceValid(src))
					? camera + src.GlobalPosition
					: camera + new Vector2(300f, 260f);
			}
			catch
			{
				useAnime = false;
			}
		}

		// ① 先把还在飞的动画全部落地，再清空当前已选。
		//
		// ★ 顺序很重要：`CreateAnime` 会把新卡 `Visible = false` 挂一个 0.5s 的 Tween，
		//   完成回调 `CompletePacketAnimation` 才把目标卡设回可见。若我们在动画还没结束时
		//   就 `DeleteAllPacket()`，那些卡会被 `ReturnPacketToPool()` 回收，而 Tween 回调
		//   仍持有它们 ⇒ 之后会把**已回收、可能已重新绑定到别的 config** 的卡设成可见。
		//   `ClearAnimeNode()` 正是原生 `PacketListChoose` 的第一步：把所有 pending 动画
		//   立即结算（目标卡可见 + 释放动画副本），从而杜绝悬空回调。
		if (useAnime)
		{
			try { bank.ClearAnimeNode(); } catch { }
		}

		// ② 放「选卡」音效。
		//
		// ★ 原生 `PacketListChoose` IL_0000-IL_0016 一进来就放这一声：
		//     AudioManager.Instance.AudioPlay("PacketPick", AudioManagerEnum.TYPE.SFX,
		//                                     0.0, true, false)
		//   我们是**整次重建**，把原生那一趟整个绕过了 ⇒ 不自己放就没有声音。
		//   原生是每次调用放**一声**（不是每张卡一声），所以这里也只放一次。
		PlayPacketPickSfx();

		// ③ 清空当前已选（与原生 `ReSelectButtonPressed` 的第一步一致）。
		seedBank.DeleteAllPacket();

		// ② 按存档顺序逐张重建。
		foreach (string key in keys)
		{
			if (!seedBank.CanAddPacket())
			{
				break;   // 卡槽满（引擎自己的上限）
			}

			TowerDefensePacketConfig cfg = null;
			try { cfg = TowerDefenseManager.GetPacketConfig(key); } catch { }
			if (cfg == null || !GodotObject.IsInstanceValid(cfg))
			{
				continue;   // 该项取不到配置（卡池变了等），跳过
			}

			int before = 0;
			try { before = seedBank.packetList.Count; } catch { }

			if (useAnime)
			{
				try { bank.CreateAnime(cfg, from); } catch { }
			}

			int after = before;
			try { after = seedBank.packetList.Count; } catch { }
			if (after <= before)
			{
				// 动画入口没生效（`animeNode` 未就绪等，`CreateAnime` 会**静默 return**）
				// ⇒ 退化为直接入槽。
				//
				// ★ `AddPacket` 的第二个参数决定它做哪一套（IL_0094 起的分支）：
				//     false → 只挂 `DeletePacket`（点一下把这张卡取消），**不**初始化；
				//     true  → 置 `alive`/`start` + 挂 `PacketPickControl.PickPacket` + `StartInit()`。
				//   `true` 那套是**关卡运行中**点卡种植用的。这里是选卡界面，语义应对应
				//   原生选卡路径（`CreateAnime` 传的也是 `IsGameRunning`，选卡时为 false）
				//   ⇒ 用 `false`，卡片点一下即可取消，和玩家自己选卡后的行为一致。
				//   但 `false` 分支不碰 `Visible`，所以必须自己把它显出来（`CreateAnime`
				//   是靠 Tween 结束回调把目标卡设可见的，我们跳过了动画就得自己设）。
				try
				{
					TowerDefenseInGamePacketShow card = seedBank.AddPacket(cfg, false);
					if (card != null && GodotObject.IsInstanceValid(card))
					{
						try { card.Visible = true; } catch { }
					}
				}
				catch (Exception ex)
				{
					if (!_reselectErrLogged)
					{
						_reselectErrLogged = true;
						Log("逐张恢复时出错（本条只报一次）：" + ex.Message);
					}
				}
			}
		}

		// ③ 数**真实入槽**的本卡张数（而不是调用次数）。
		int gotMine = 0;
		try
		{
			Godot.Collections.Array<TowerDefenseInGamePacketShow> list = seedBank.packetList;
			if (list != null)
			{
				for (int i = 0; i < list.Count; i++)
				{
					TowerDefenseInGamePacketShow c = list[i];
					if (c != null && GodotObject.IsInstanceValid(c) && IsMyCard(c))
					{
						gotMine++;
					}
				}
			}
		}
		catch { }

		// ④ 把已选中的池卡置灰（原生「开关式选卡」的另一半，见方法内注释）。
		GreyOutSelectedPoolCards(feature, seedBank);

		return gotMine;
	}

	/// <summary>
	/// 重建卡槽之后，把「已经在卡槽里」的那些池卡置灰（<c>alive = false</c>）。
	///
	/// ★ 为什么必须补这一步（IL 证据）：原生点卡走
	///   <c>TowerDefenseBattleFeaturePacketBank.PacketChoose</c>，它在
	///   IL_00e4 调 <c>CreateAnime</c> 之后紧跟 IL_00ea <c>set_alive(false)</c>
	///   —— 这就是「选中的卡在卡池里变黑、再点一次取消」那套开关模式的来源。
	///   本 Mod 的整次重建直接调 <c>CreateAnime</c> / <c>AddPacket</c>，
	///   绕过了 <c>PacketChoose</c> ⇒ 卡槽恢复对了，但卡池里那些卡**全亮着**，
	///   看起来像「什么都没选上」，而且再点一次也不会取消。
	///   （只有本卡例外：它被无限选取接管，本来就该一直可点。）
	///
	/// ★ 判据用 <c>seedBank.HasPacket(key)</c>（卡槽**实况**），不用传进来的期望列表：
	///   重建可能因为「卡槽已满 break」「某项取不到配置 continue」而少放几张，
	///   按期望列表置灰会把**没真正选上**的卡也弄黑，玩家就点不动它了。
	///   这也与原生 <c>BindVirtualizedPacket</c>（IL_007d <c>HasPacket</c> →
	///   IL_0088 <c>set_alive</c>）和 <c>PacketChooseFromName</c> 的判据一致。
	///
	/// ★ 只做「置灰」，不做「恢复变亮」：恢复是原生负责的 ——
	///   上面 <c>DeleteAllPacket()</c> 会对每张被移除的卡调 <c>PacketAlive(key)</c>
	///   把池卡重新点亮。所以到这里为止所有池卡都是亮的，只需把该灰的灰掉。
	///   刻意不主动点亮任何卡，免得把引擎因别的原因（未解锁 / 锁卡）置灰的卡错误点亮。
	/// </summary>
	private void GreyOutSelectedPoolCards(
		TowerDefenseBattleFeaturePacketBank feature, TowerDefenseInGameSeedBank seedBank)
	{
		try
		{
			if (feature == null || !GodotObject.IsInstanceValid(feature))
			{
				return;
			}
			if (seedBank == null || !GodotObject.IsInstanceValid(seedBank))
			{
				return;
			}
			Godot.Collections.Array<TowerDefenseInGamePacketShow> pool = feature.packetList;
			if (pool == null)
			{
				return;
			}
			for (int i = 0; i < pool.Count; i++)
			{
				TowerDefenseInGamePacketShow card = pool[i];
				if (card == null || !GodotObject.IsInstanceValid(card))
				{
					continue;
				}
				string key = KeyOf(card);
				if (string.IsNullOrEmpty(key) || key == MyKey)
				{
					continue;   // 本卡由无限选取保持可点，不能灰
				}
				bool selected;
				try { selected = seedBank.HasPacket(key); } catch { continue; }
				if (!selected)
				{
					continue;   // 没真正进卡槽 ⇒ 保持可点
				}
				try
				{
					if (card.alive)
					{
						card.alive = false;   // 与原生 PacketChoose IL_00ea 一致
					}
				}
				catch { }
			}
		}
		catch (Exception ex)
		{
			if (_reselectErrLogged)
			{
				return;
			}
			_reselectErrLogged = true;
			Log("恢复池卡置灰状态时出错（本条只报一次）：" + ex.Message);
		}
	}

	/// <summary>
	/// 按下「重选」之后的几帧里核对卡槽：若被原生那条 Godot 连线覆盖过，就按存档顺序重建。
	///
	/// ★ 触发条件刻意收窄成「**本卡张数变少**」而不是「序列不完全一致」：
	///   玩家按下重选后可能马上又点了几张别的卡，那种情况本卡张数不会减少；
	///   只有原生覆盖才会把重复的本卡塌成 1 张。用张数判定就不会误伤玩家的新选择。
	/// </summary>
	private void DriveReselectReconcile()
	{
		if (_reselectDesired == null)
		{
			return;
		}
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				_reselectDesired = null;
				return;
			}
			TowerDefenseInGameSeedBank seedBank = mgr.GetSeedBank();
			if (seedBank == null || !GodotObject.IsInstanceValid(seedBank))
			{
				_reselectDesired = null;
				return;
			}

			if (CountMine(seedBank) >= _reselectDesiredMine)
			{
				// 本卡份数够（玩家可能又加了别的卡，不管）⇒ 连续几帧都稳定就收工。
				_reselectWatch--;
				if (_reselectWatch <= 0)
				{
					_reselectDesired = null;
				}
				return;
			}

			if (_reselectAttempts >= 4)
			{
				_reselectDesired = null;   // 重建多次仍不符，放弃（避免死循环）
				return;
			}

			int gotMine = ApplyReselect(mgr, _reselectDesired);
			_reselectAttempts++;
			_reselectWatch = 3;

			if (!_reselectRebuildLogged)
			{
				_reselectRebuildLogged = true;
				Log("「重选」结果被原生路径覆盖，已按存档顺序重建（本卡 " + gotMine + " 张）。");
			}
			if (gotMine <= 0)
			{
				_reselectDesired = null;
			}
		}
		catch (Exception ex)
		{
			_reselectDesired = null;
			Swallow("重选结果核对", ex);
		}
	}

	/// <summary>数当前卡槽里本卡的张数。</summary>
	private static int CountMine(TowerDefenseInGameSeedBank seedBank)
	{
		int n = 0;
		try
		{
			Godot.Collections.Array<TowerDefenseInGamePacketShow> list = seedBank.packetList;
			if (list != null)
			{
				for (int i = 0; i < list.Count; i++)
				{
					TowerDefenseInGamePacketShow c = list[i];
					if (c != null && GodotObject.IsInstanceValid(c) && IsMyCard(c))
					{
						n++;
					}
				}
			}
		}
		catch { }
		return n;
	}

	/// <summary>
	/// 放一声原生「选卡」音效（`PacketPick`），与 `PacketListChoose` 开头那一声一致。
	///
	/// ★ 走反射而不是直接 `AudioManager.Instance.AudioPlay(...)`，有两个原因：
	///   1. `AudioPlay` 的第二个参数类型是嵌套枚举 `AudioManagerEnum.TYPE`（值 SFX = 1）。
	///      用反射就不需要在编译期引用这个嵌套类型，少一层对游戏内部结构的耦合；
	///   2. 万一某个版本改了签名 / 改了枚举值，反射失败只会**没声音**，
	///      不会抛异常影响「重选」本身（音效是锦上添花，绝不能拖垮主流程）。
	/// </summary>
	private void PlayPacketPickSfx()
	{
		try
		{
			Type amType = null;
			foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				try
				{
					amType = asm.GetType("AudioManager", false);
					if (amType != null)
					{
						break;
					}
				}
				catch { }
			}
			if (amType == null)
			{
				return;
			}

			object inst = null;
			PropertyInfo ip = amType.GetProperty("Instance",
				BindingFlags.Public | BindingFlags.Static);
			if (ip != null)
			{
				inst = ip.GetValue(null);
			}
			if (inst == null)
			{
				FieldInfo iff = amType.GetField("Instance",
					BindingFlags.Public | BindingFlags.Static);
				if (iff != null)
				{
					inst = iff.GetValue(null);
				}
			}
			if (inst == null)
			{
				return;   // 音频系统还没起来
			}

			MethodInfo play = null;
			foreach (MethodInfo m in amType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
			{
				if (m.Name != "AudioPlay")
				{
					continue;
				}
				ParameterInfo[] ps = m.GetParameters();
				// (string, TYPE, double, bool, bool)
				if (ps.Length == 5 && ps[0].ParameterType == typeof(string)
					&& ps[1].ParameterType.IsEnum && ps[2].ParameterType == typeof(double)
					&& ps[3].ParameterType == typeof(bool) && ps[4].ParameterType == typeof(bool))
				{
					play = m;
					break;
				}
			}
			if (play == null)
			{
				return;
			}

			ParameterInfo[] prm = play.GetParameters();
			object sfx = Enum.ToObject(prm[1].ParameterType, 1);   // TYPE.SFX = 1
			play.Invoke(inst, new object[] { "PacketPick", sfx, 0.0d, true, false });
		}
		catch (Exception ex)
		{
			Swallow("播放选卡音效", ex);
		}
	}

	// ================================================================ 选卡分组（接管读取）

	/// <summary>
	/// 每帧看一眼 6 个「选卡分组」按钮，把每个的 `OnLoadGroup` **整体替换**成我们的处理器。
	///
	/// ★ 原生 `LoadPacketGroup(id)` IL_008a-IL_00a1 与「重选上次卡牌」是**同一条**路径：
	///     seedBank.DeleteAllPacket();  PacketListChoose(存档);
	///   而 `PacketListChoose` 用按 saveKey 去重的 `packetNameSet` 判「已有」⇒
	///   分组里存了 5 张本卡，读回来只进 1 张。
	///
	/// ★ 保存侧**不动**：`SavePacketGroup(id)` 直接遍历 `packetList` 逐张写 key，
	///   本来就能正确保存重复卡。
	///
	/// ★ 按钮的取法与游戏自己一致（`_ConnectPacketBankSignals` IL_0191-IL_01ea）：
	///     packetBank.translate.GetNode("PacketGroup")           ← 容器
	///     → GetNode("PacketGroupButton")                         ← id = 1（没有数字后缀）
	///     → GetNode("PacketGroupButton" + i)  for i = 2..6
	///   即 1 号是 `PacketGroupButton`，2~6 号带数字后缀。
	/// </summary>
	private void DriveGroupHook()
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			TowerDefenseBattleFeaturePacketBank feature = mgr.GetPacketBankFeature();
			if (feature == null || !GodotObject.IsInstanceValid(feature))
			{
				return;   // 不在选卡界面
			}
			MethodInfo handler = typeof(RandomImitaterEntry).GetMethod(
				nameof(OnGroupLoadPressed),
				BindingFlags.NonPublic | BindingFlags.Instance);
			if (handler == null)
			{
				return;
			}

			for (int id = 1; id <= GroupCount; id++)
			{
				Node btn = FindGroupButton(feature, id);
				if (btn == null || !GodotObject.IsInstanceValid(btn))
				{
					continue;
				}
				// 读**活委托**判断，不按实例 ID 记账：游戏可能重建按钮并重新挂处理器。
				Delegate curD = GetEventField(btn, "OnLoadGroup") as Delegate;
				if (curD != null)
				{
					Delegate[] inv = curD.GetInvocationList();
					if (inv.Length == 1 && inv[0].Target == this
						&& inv[0].Method.Name == nameof(OnGroupLoadPressed))
					{
						continue;   // 已经是「只有我们」的状态
					}
				}
				Delegate kept = StripMine(curD, nameof(OnGroupLoadPressed));
				if (!ReplaceEventField(btn, "OnLoadGroup", handler))
				{
					continue;
				}
				_groupOriginal[btn.GetInstanceId()] = kept;
				if (!_groupLogged)
				{
					_groupLogged = true;
					Log("已接管「读取选卡分组」按钮（6 个分组，多张本卡按存档顺序精确恢复）。");
				}
			}
		}
		catch (Exception ex)
		{
			if (!_groupErrLogged)
			{
				_groupErrLogged = true;
				Log("接管「选卡分组」按钮失败（已吞）：" + ex.Message);
			}
		}
	}

	/// <summary>
	/// 找第 <paramref name="id"/> 个分组按钮（1..6）。
	/// 与游戏 `_ConnectPacketBankSignals` IL_0191-IL_01ea 的取法一致：
	/// 容器是 `packetBank.translate/PacketGroup`，1 号叫 `PacketGroupButton`，
	/// 2..6 号叫 `PacketGroupButton2` … `PacketGroupButton6`。
	/// </summary>
	private static Node FindGroupButton(TowerDefenseBattleFeaturePacketBank feature, int id)
	{
		try
		{
			object bank = GetMember(feature, "packetBank");
			if (bank == null || !GodotObject.IsInstanceValid((GodotObject)bank))
			{
				return null;
			}
			object translate = GetMember(bank, "translate");
			Control tr = translate as Control;
			if (tr == null || !GodotObject.IsInstanceValid(tr))
			{
				return null;
			}
			Node group = tr.GetNodeOrNull("PacketGroup");
			if (group == null || !GodotObject.IsInstanceValid(group))
			{
				return null;
			}
			string name = (id <= 1) ? "PacketGroupButton" : ("PacketGroupButton" + id);
			return group.GetNodeOrNull(name);
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// 某个分组被「读取」：我们自己按存档顺序整次重建（份数与顺序都对）。
	/// 判据与收窄：只有存档里本卡 ≥2 张时才走自定义路径；否则原样调用原生处理器。
	/// 任何异常都退回原生处理器 ⇒ 最坏情况 = 原生行为，按钮不会失灵。
	/// </summary>
	private void OnGroupLoadPressed(int id)
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				CallOriginalGroupLoad(id);
				return;
			}

			Godot.Collections.Array saved = ReadGroupSave(mgr, id);
			int mine = 0;
			if (saved != null)
			{
				foreach (Variant v in saved)
				{
					if (string.Equals(v.AsString(), MyKey, StringComparison.Ordinal))
					{
						mine++;
					}
				}
			}
			if (mine <= 1)
			{
				// 本卡只有 0~1 张：原生的结果就是对的，完全交给原生。
				CallOriginalGroupLoad(id);
				return;
			}

			// 与「重选」同一套：期望状态 = 存档里配置仍有效的 key（保持顺序）。
			List<string> desired = new List<string>();
			if (saved != null)
			{
				foreach (Variant v in saved)
				{
					string key = v.AsString();
					if (string.IsNullOrEmpty(key))
					{
						continue;
					}
					TowerDefensePacketConfig cfg = null;
					try { cfg = TowerDefenseManager.GetPacketConfig(key); } catch { }
					if (cfg == null || !GodotObject.IsInstanceValid(cfg))
					{
						continue;
					}
					desired.Add(key);
				}
			}
			if (desired.Count == 0)
			{
				CallOriginalGroupLoad(id);
				return;
			}

			int gotMine = ApplyReselect(mgr, desired);
			if (gotMine < mine)
			{
				Log("「读取分组 " + id + "」接管结果不符（本卡 " + gotMine + "/" + mine + "），回退原生处理器。");
				CallOriginalGroupLoad(id);
				return;
			}

			// 与「重选」同理：按下后仍可能被另一条原生路径覆盖，盯几帧核对。
			_reselectDesired = desired;
			_reselectDesiredMine = mine;
			_reselectWatch = 6;
			_reselectAttempts = 0;

			if (!_groupLogged2)
			{
				_groupLogged2 = true;
				Log("「读取分组」接管生效：分组 " + id + "，存档 " + desired.Count
					+ " 项 → 入槽，其中本卡 " + gotMine + " 张（存档记忆 " + mine + " 张）。");
			}
		}
		catch (Exception ex)
		{
			if (!_groupErrLogged)
			{
				_groupErrLogged = true;
				Log("「读取分组」接管异常，回退原生处理器（本条只报一次）：" + ex.Message);
			}
			CallOriginalGroupLoad(id);
		}
	}

	/// <summary>读第 <paramref name="id"/> 个分组的存档数组（按模式选前缀，与原生一致）。</summary>
	private Godot.Collections.Array ReadGroupSave(TowerDefenseManager mgr, int id)
	{
		try
		{
			if (GameSaveManager.Instance == null
				|| !GodotObject.IsInstanceValid(GameSaveManager.Instance))
			{
				return null;
			}
			string prefix = GroupKeyPrefix;
			try
			{
				if (mgr != null && GodotObject.IsInstanceValid(mgr)
					&& (mgr.IsIZMMode() || mgr.IsIZM2Mode()))
				{
					prefix = GroupKeyPrefixIzm;
				}
			}
			catch { }

			// 先按模式定的前缀取，取不到再退回另一个前缀（保证「至少能用」）。
			foreach (string p in new[] { prefix, prefix == GroupKeyPrefix ? GroupKeyPrefixIzm : GroupKeyPrefix })
			{
				try
				{
					Variant v = GameSaveManager.Instance.GetKeyValue(p + id);
					Godot.Collections.Array a = v.AsGodotArray();
					if (a != null && a.Count > 0)
					{
						return a;
					}
				}
				catch { }
			}
		}
		catch { }
		return null;
	}

	/// <summary>
	/// 调用一个委托（把它原本的目标方法按顺序调一遍）。成功返回 true。
	///
	/// ★ 为什么不用 <c>Delegate.DynamicInvoke</c>：游戏发布包裁剪过 BCL，运行时里
	///   根本没有这个方法（实测日志：`Method not found: 'System.Object
	///   System.Delegate.DynamicInvoke(System.Object[])'`）。更麻烦的是「缺方法」抛在
	///   **调用方方法的 JIT 阶段** ⇒ 方法体里的 try/catch 拦不住，会把整个兜底函数
	///   一起打断（表现就是：卡组里没有本卡时，「重选 / 读取分组」按钮彻底失灵）。
	///   这里改成反射调目标方法 —— `MethodInfo.Invoke` / `Delegate.Method` /
	///   `GetInvocationList` 实测都在（接管逻辑本身就靠它们跑通的）。
	/// </summary>
	private static bool InvokeDelegate(Delegate d, object[] args)
	{
		if (d == null)
		{
			return false;
		}
		try
		{
			Delegate[] inv = d.GetInvocationList();
			for (int i = 0; i < inv.Length; i++)
			{
				MethodInfo mi = inv[i].Method;
				if (mi == null)
				{
					return false;
				}
				mi.Invoke(inv[i].Target, args);
			}
			return true;
		}
		catch
		{
			// 底稿可能是上一个场景留下的、目标已释放的委托 ⇒ 交回调用方走原生方法兜底。
			return false;
		}
	}

	/// <summary>调用第 <paramref name="id"/> 个分组按钮被我们换下来的原生 `OnLoadGroup`（兜底）。</summary>
	private void CallOriginalGroupLoad(int id)
	{
		// ① 有底稿就直接调（原生行为，逐字一致）。
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr != null && GodotObject.IsInstanceValid(mgr))
			{
				TowerDefenseBattleFeaturePacketBank feature = mgr.GetPacketBankFeature();
				if (feature != null && GodotObject.IsInstanceValid(feature))
				{
					Node btn = FindGroupButton(feature, id);
					if (btn != null && GodotObject.IsInstanceValid(btn))
					{
						Delegate d;
						if (_groupOriginal.TryGetValue(btn.GetInstanceId(), out d)
							&& InvokeDelegate(d, new object[] { id }))
						{
							return;
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			Swallow("调用原生「读取分组」底稿", ex);
		}

		// ② 没底稿 / 底稿失效：直接调游戏自己的 LoadPacketGroup(id)（同样是原生行为）。
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			TowerDefenseBattleFeaturePacketBank feature = mgr.GetPacketBankFeature();
			if (feature == null || !GodotObject.IsInstanceValid(feature))
			{
				return;
			}
			MethodInfo m = null;
			for (Type t = feature.GetType(); t != null && m == null; t = t.BaseType)
			{
				m = t.GetMethod("LoadPacketGroup",
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
					null, new[] { typeof(int) }, null);
			}
			if (m != null)
			{
				m.Invoke(feature, new object[] { id });
			}
		}
		catch (Exception ex)
		{
			Swallow("兜底调用原生「读取分组」", ex);
		}
	}

	/// <summary>
	/// 从多播委托里剥掉**我们自己**的处理器，返回剩下的部分（= 原生处理器）。
	/// 用于把按钮字段收敛成「只有我们」，同时保住原生委托作兜底。
	/// </summary>
	private Delegate StripMine(Delegate d, string handlerName)
	{
		if (d == null)
		{
			return null;
		}
		try
		{
			Delegate keep = null;
			foreach (Delegate one in d.GetInvocationList())
			{
				bool mine = (one.Target == this && one.Method.Name == handlerName);
				if (!mine)
				{
					keep = Delegate.Combine(keep, one);
				}
			}
			return keep;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>调用被我们替换下来的原生「重选」处理器（兜底）。</summary>
	private void CallOriginalReselect()
	{
		// ★ 这里绝不能用 `Delegate.DynamicInvoke`：游戏发布包裁掉了它，而且「缺方法」
		//   抛在本方法的 JIT 阶段（方法内的 try/catch 拦不住）⇒ 整个兜底失效、
		//   按钮彻底失灵。改用 InvokeDelegate（反射调目标方法）。
		if (InvokeDelegate(_reselectOriginal, new object[0]))
		{
			return;
		}
		// ★ 没有底稿（我们比原生先挂上 / 原生这次还没连 / 底稿已失效）时，
		//   若直接 return，按钮会**彻底失灵** —— 绝不能这样。
		//   这里自己复刻一遍原生行为：`DeleteAllPacket()` + `PacketListChoose(存档)`，
		//   与 `ReSelectButtonPressed` 的 IL 完全一致。
		NativeReselectFallback();
	}

	/// <summary>
	/// 复刻原生 `ReSelectButtonPressed` 作为最后兜底。
	///
	/// ★ 优先直接调用游戏自己的 `ReSelectButtonPressed()` —— 那是**逐字**的原生行为，
	///   连「按模式选哪个存档键」这种细节都不会走样。只有连它也调不到时，
	///   才退到下面手工复刻（读存档 → `DeleteAllPacket()` → `PacketListChoose(存档)`）。
	/// </summary>
	private void NativeReselectFallback()
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			TowerDefenseBattleFeaturePacketBank feature = mgr.GetPacketBankFeature();
			if (feature == null || !GodotObject.IsInstanceValid(feature))
			{
				return;
			}

			// ① 最好：直接调游戏的原生方法（它自己会按 IZM / 普通模式选存档键）。
			MethodInfo native = null;
			for (Type t = feature.GetType(); t != null && native == null; t = t.BaseType)
			{
				native = t.GetMethod("ReSelectButtonPressed",
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
					null, Type.EmptyTypes, null);
			}
			if (native != null)
			{
				native.Invoke(feature, null);
				return;
			}

			// ② 退路：手工复刻。
			Godot.Collections.Array saved = ReadReselectSave();
			if (saved == null || saved.Count == 0)
			{
				return;
			}
			TowerDefenseInGameSeedBank seedBank = mgr.GetSeedBank();
			if (seedBank != null && GodotObject.IsInstanceValid(seedBank))
			{
				seedBank.DeleteAllPacket();
			}
			MethodInfo plc = null;
			for (Type t = feature.GetType(); t != null && plc == null; t = t.BaseType)
			{
				plc = t.GetMethod("PacketListChoose",
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			}
			if (plc == null)
			{
				return;
			}
			plc.Invoke(feature, new object[] { saved });
		}
		catch (Exception ex)
		{
			Swallow("复刻原生「重选」", ex);
		}
	}

	/// <summary>
	/// 读存档里「上次卡牌选择」的数组。
	///
	/// ★ 键的选择必须跟游戏**完全一致**，而不是「取第一个非空的」：
	///   `ReSelectButtonPressed` IL_0000-IL_0049 是按模式二选一 ——
	///     `IsIZMMode() || IsIZM2Mode()` 为真 → `ZombiePacketReSlect`；
	///     否则                              → `PacketReSlect`。
	///   两个键可能**同时**有内容（先玩普通关、再玩 IZM 就会这样）。
	///   若按「第一个非空」取，在 IZM 关里会读到普通模式那份存档，
	///   恢复出来的卡与原生完全不同 ⇒ 玩家会看到莫名其妙的卡组。
	/// </summary>
	private Godot.Collections.Array ReadReselectSave()
	{
		try
		{
			if (GameSaveManager.Instance == null
				|| !GodotObject.IsInstanceValid(GameSaveManager.Instance))
			{
				return null;
			}

			// 先按模式定键（与原生一致），取不到再退回另一个键，保证「至少能用」。
			string primary = ReSlectKey;
			try
			{
				TowerDefenseManager mgr = TowerDefenseManager.Instance;
				if (mgr != null && GodotObject.IsInstanceValid(mgr)
					&& (mgr.IsIZMMode() || mgr.IsIZM2Mode()))
				{
					primary = ReSlectKeyIzm;
				}
			}
			catch { }

			foreach (string k in new[] { primary, primary == ReSlectKey ? ReSlectKeyIzm : ReSlectKey })
			{
				try
				{
					Variant v = GameSaveManager.Instance.GetKeyValue(k);
					Godot.Collections.Array a = v.AsGodotArray();
					if (a != null && a.Count > 0)
					{
						return a;
					}
				}
				catch { }
			}
		}
		catch { }
		return null;
	}

	// ================================================================ 游戏内设置（即时生效）

	/// <summary>
	/// 每帧检查一次游戏内设置有没有被改。发现变化就**立刻重建混合卡池**，
	/// 玩家在「Mod 管理 → 配置」里改完点保存，无需重启、无需退出关卡即生效。
	///
	/// ★ 为什么用轮询而不是订阅 <c>XWModSettings.Changed</c>：
	///   事件只在保存那一刻触发一次。若本 Mod 加载得比设置激活晚（或激活发生在
	///   Initialize 之前），订阅就永远等不到那次通知，表现成「改了没反应」。
	///   轮询每帧比对几个 int/bool，开销可忽略，且不依赖任何时序假设。
	///
	/// ★ 比较用**原始值**（int / bool），不比较换算后的 double：
	///   百分比→概率会引入浮点误差，用 double 比会出现「永远判定为变化」的死循环。
	/// </summary>
	private void DriveSettingsWatch()
	{
		try
		{
			if (_settings == null)
			{
				return;   // 拿不到配置：一直用默认值，没什么可监视的
			}
			ReadSettings(false);
		}
		catch (Exception ex)
		{
			if (!_settingsWarned)
			{
				_settingsWarned = true;
				Log("设置监视异常（本条只报一次，将继续使用上次的值）：" + ex.Message);
			}
		}
	}

	/// <summary>
	/// 从 <see cref="_settings"/> 读出配置项并换算成本 Mod 的运行时参数。
	/// 「出植物概率」变化 ⇒ 重建混合卡池；「彩色化」变化 ⇒ 立刻对场上产物生效。
	/// </summary>
	/// <param name="initial">
	/// true = 初始化阶段（打日志、不做「变化」判断）；false = 每帧轮询。
	/// </param>
	private void ReadSettings(bool initial)
	{
		try
		{
			int rawChance = ReadInt(KeyPlantChance, (int)Math.Round(DefaultPlantChance * 100.0));
			bool rawColorize = ReadBool(KeyColorize, true);
			int rawCost = ReadInt(KeySunCost, DefaultSunCost);

			// 防御：清单写错导致越界时夹到合法区间，绝不让坏值传到卡池构建里。
			if (rawChance < 0) { rawChance = 0; }
			if (rawChance > 100) { rawChance = 100; }
			if (rawCost < 0) { rawCost = 0; }
			if (rawCost > MaxSunCost) { rawCost = MaxSunCost; }

			bool chanceChanged = rawChance != _rawPlantChance;
			bool colorizeChanged = rawColorize != _rawColorize;
			bool costChanged = rawCost != _rawSunCost;
			bool changed = chanceChanged || colorizeChanged || costChanged;

			if (!changed && !initial)
			{
				return;
			}

			_rawPlantChance = rawChance;
			_rawColorize = rawColorize;
			_rawSunCost = rawCost;

			_plantChance = rawChance / 100.0;
			ColorizeResult = rawColorize;   // 静态镜像：供伴随脚本类逐帧读取

			// ★ 图鉴概率文字与阳光消耗都要跟着配置走（它们是 .tres 里的静态值）。
			//   走到这里只可能是「初始化」或「值真的变了」，所以不会每帧白跑。
			//   资源还没加载时保持待同步，由 OnFrame 逐帧补做，成功一次即停。
			_presentationDirty = !SyncCardPresentation(rawChance, rawCost);

			if (initial)
			{
				if (!_settingsLogged)
				{
					_settingsLogged = true;
					Log("已读取游戏内配置：出植物 " + rawChance + "%、阳光消耗 "
						+ rawCost + "、彩色化 " + (rawColorize ? "开" : "关")
						+ "（BOSS / 巨人始终允许）。"
						+ "可在游戏内「Mod 管理 → 配置」里实时修改。");
				}
				return;
			}

			// 彩色化开关变了 ⇒ 下一帧 TickAllColorize 就会把新取值应用到场上所有产物
			// （那里每帧都按当前开关重设一遍，所以无需在这里额外做什么）。
			if (colorizeChanged)
			{
				Log("检测到「彩色化」改为 " + (rawColorize ? "开" : "关")
					+ " ⇒ 立即应用到场上所有随机模仿者产物。");
			}

			if (costChanged)
			{
				Log("检测到阳光消耗改为 " + rawCost + " ⇒ 已同步到卡牌与图鉴。");
			}

			// 概率变了 ⇒ 重建卡池（只影响之后种下的）。
			if (chanceChanged)
			{
				_rebuildCount++;
				_bankOk = false;
				Log("检测到出植物概率变化（第 " + _rebuildCount + " 次）：" + rawChance
					+ "% ⇒ 立即重建混合卡池。");
			}
		}
		catch (Exception ex)
		{
			Log("读取设置失败（继续用当前值）：" + ex.Message);
		}
	}

	/// <summary>读一个整数设置项；缺失/类型不符时返回 <paramref name="fallback"/>。</summary>
	private int ReadInt(string key, int fallback)
	{
		try
		{
			if (_settings == null)
			{
				return fallback;
			}
			// ★ 用框架公开的 Get<T>()，而不是反射读 internal 的 Values：
			//   Get<T> 缺失键会抛 KeyNotFoundException、类型不符会抛 InvalidCastException，
			//   都在这里接住并回退默认值 —— 玩家把清单改坏也不会让 Mod 失效。
			return _settings.Get<int>(key);
		}
		catch
		{
			return fallback;
		}
	}

	/// <summary>读一个布尔设置项；缺失/类型不符时返回 <paramref name="fallback"/>。</summary>
	private bool ReadBool(string key, bool fallback)
	{
		try
		{
			if (_settings == null)
			{
				return fallback;
			}
			return _settings.Get<bool>(key);
		}
		catch
		{
			return fallback;
		}
	}

	/// <summary>
	/// 图鉴里那两句概率文案的锚点。用「纯字符串定位」而不是正则：
	/// 游戏的发布包裁剪过 BCL，<c>Regex.Replace(string, string, int)</c> 这个重载
	/// 在运行时根本不存在（实测日志：Method not found），而且它是在**方法 JIT 阶段**
	/// 抛出的 —— 连方法内部的 try/catch 都拦不住，会把整条同步链路一起打断。
	/// 所以这里只用 IndexOf / 逐字符扫描。
	/// </summary>
	private const string HandbookPlantAnchor = "概率开出随机植物";
	private const string HandbookZombieAnchor = "概率开出随机僵尸";

	/// <summary>
	/// 把图鉴原文里的两个百分比换成 <paramref name="plantPercent"/> 与其余数。
	/// 定位不到锚点（官方改了文案）就返回 null ⇒ 调用方不动图鉴，绝不写坏。
	///
	/// ★ 只用 IndexOf / 逐字符比较 + 字符串拼接，刻意不引入 Regex / StringBuilder /
	///   IFormatProvider 之类：游戏发布包裁剪过 BCL，缺哪个重载就得等运行时才知道，
	///   而缺方法异常发生在**方法 JIT 阶段**，方法内的 try/catch 拦不住。
	/// </summary>
	private static string BuildHandbookText(string original, int plantPercent)
	{
		if (string.IsNullOrEmpty(original))
		{
			return null;
		}
		int a = original.IndexOf(HandbookPlantAnchor, StringComparison.Ordinal);
		if (a < 0)
		{
			return null;
		}
		int b = original.IndexOf(HandbookZombieAnchor,
			a + HandbookPlantAnchor.Length, StringComparison.Ordinal);
		if (b < 0)
		{
			return null;
		}

		// 期望形态：「…<数字A>%<锚点1>…<数字B>%<锚点2>…」
		//   替换段1 = [digitsA, a)   —— 数字A 加它后面那个 '%'（在 a-1）
		//   中间段  = [a, digitsB)   —— 锚点1 到数字B 之前，不含数字B 与它的 '%'
		//   替换段2 = [digitsB, b)   —— 数字B 加它后面那个 '%'（在 b-1）
		int digitsA = ScanBackPercentStart(original, a);
		int digitsB = ScanBackPercentStart(original, b);
		if (digitsA < 0 || digitsB < 0 || digitsB <= a)
		{
			return null;
		}

		// 中文区域设置下数字就是 ASCII，直接拼接即可，不必指定格式提供者。
		return original.Substring(0, digitsA) + plantPercent + "%"
			+ original.Substring(a, digitsB - a) + (100 - plantPercent) + "%"
			+ original.Substring(b);
	}

	/// <summary>
	/// 从锚点 <paramref name="anchor"/> 往回找数字段的**起点**。
	/// 形态必须是「数字 + % + 锚点」（'%' 恰好位于 anchor-1），不合形态返回 -1。
	/// </summary>
	private static int ScanBackPercentStart(string s, int anchor)
	{
		if (anchor <= 0 || s[anchor - 1] != '%')
		{
			return -1;   // 锚点前不是 '%'
		}
		int start = anchor - 1;   // 先指向 '%'
		while (start > 0 && s[start - 1] >= '0' && s[start - 1] <= '9')
		{
			start--;
		}
		return start == anchor - 1 ? -1 : start;   // '%' 前没有数字 ⇒ 形态不对
	}

	/// <summary>
	/// 把「本卡的展示与花费」同步成当前配置值：图鉴概率文字 + 阳光消耗。
	///
	/// ★ 为什么必须做：图鉴的 <c>handbookDescribe</c> 与 <c>characterConfig.cost</c>
	///   都是打包进 <c>Resources/*.tres</c> 的**静态值**，不会自己反映运行时配置。
	///   不同步就会出现「实际按 70% 出、图鉴仍写 65%」「实际收 200 阳光、图鉴写 0」
	///   这种自相矛盾。
	///
	/// ★ 必须用 <c>GetPacketConfigReadOnly</c>，不能用 <c>GetPacketConfig</c>
	///   （这条是踩过坑之后用 IL 确认的）：
	///   · <c>GetPacketConfig</c>：IL_0175 起 <c>Resource.Duplicate(true)</c> ⇒
	///     返回的是**副本**，改它对本体毫无影响（图鉴不动就是这个原因）。
	///   · <c>GetPacketConfigReadOnly</c>：IL_0017 取 Variant → IL_0019
	///     <c>AsGodotObject</c> → IL_001e <c>castclass</c> 后**直接返回**，
	///     即 ResourceManager 里那份**共享实例**；<c>Almanac.InitPlant</c> 用的
	///     正是它 ⇒ 改这份才会反映到图鉴。
	///
	/// ★ 花费写 <c>characterConfig.cost</c> 的优先级依据
	///   （<c>GetCostBeforeModifiers</c> 的 IL）：
	///   IL_0015 先取 <c>characterConfig.cost</c>，IL_001c/IL_003e 只在
	///   <c>overrideCost</c> / <c>_override.cost</c> **不等于 -1** 时才覆盖它们。
	///   本卡这两项都没设（-1 / null），所以写 <c>characterConfig.cost</c>
	///   既管住种植扣阳光，也管住图鉴的「花费:」标签（IL_0504 直接读该字段）。
	///
	/// ★ 幂等：概率文案每次从 <see cref="_handbookOriginal"/>（首次读到的原文）重建，
	///   花费每次从 <see cref="_costOriginal"/> 出发，不在上一次结果上继续改 ⇒
	///   反复切换配置不会漂移，「恢复默认」也能干净回到打包值。
	///
	/// ★ 返回 false 表示「资源还没加载」，调用方保持待同步、稍后重试。
	/// </summary>
	private bool SyncCardPresentation(int plantPercent, int sunCost)
	{
		try
		{
			TowerDefensePacketConfig cfg
				= TowerDefenseManager.GetPacketConfigReadOnly(MyKey);
			if (cfg == null || !GodotObject.IsInstanceValid(cfg))
			{
				return false;   // 资源还没加载：保持待同步，下一帧再试
			}

			bool allOk = true;

			// ── ① 图鉴概率文字 ──
			string text = cfg.handbookDescribe;
			if (text == null)
			{
				allOk = false;   // 还没加载完，稍后重试
			}
			else
			{
				if (_handbookOriginal == null)
				{
					_handbookOriginal = text;
				}
				// 定位不到锚点（官方改了文案）就返回 null ⇒ 不动图鉴，避免写坏。
				string replaced = BuildHandbookText(_handbookOriginal, plantPercent);
				if (replaced != null && replaced != text)
				{
					cfg.handbookDescribe = replaced;
				}
			}

			// ── ② 阳光消耗 ──
			TowerDefenseCharacterConfig cc = cfg.characterConfig
				as TowerDefenseCharacterConfig;
			if (cc == null || !GodotObject.IsInstanceValid(cc))
			{
				allOk = false;
			}
			else
			{
				if (_costOriginal == int.MinValue)
				{
					_costOriginal = cc.cost;
					_costNightOriginal = cc.costNight;
				}
				int want = sunCost < 0 ? 0 : (sunCost > MaxSunCost ? MaxSunCost : sunCost);
				if (cc.cost != want)
				{
					cc.cost = want;
				}
				// ★ 夜价：GetCostBeforeModifiers 在 IL_0052-IL_005e 读 costNight，
				//   只有它 **!= -1** 且当前是夜图时才用它计价（-1 = 本卡没有夜价）。
				//   TowerDefenseCharacterConfig..ctor 在 IL_009c 把 costNight 初始化为
				//   -1，而本卡的 .tres 没写这个字段 ⇒ 它本来就是 -1，改 cost 即可
				//   日夜统一生效。这里保留一个守卫：万一将来给本卡加了夜价
				//   （costNight != -1），就跟着一起同步，免得「白天 200、夜里另一个价」。
				if (_costNightOriginal != -1 && cc.costNight != want)
				{
					cc.costNight = want;
				}
			}

			return allOk;
		}
		catch
		{
			// 展示与花费同步失败绝不影响玩法；保持待同步，稍后再试。
			return false;
		}
	}

	// ================================================================ 右键放行小推车

	/// <summary>
	/// 鼠标右键**点到某台小推车** ⇒ 让它立刻发车（原生逻辑里推车只在僵尸撞到它时才动）。
	///
	/// 用到的游戏成员全部是 public，不需要反射私有字段（IL 证据）：
	///   · <c>TowerDefenseManager.GetMowerFeature()</c>：public 实例方法，取本关的推车特性。
	///   · <c>TowerDefenseBattleFeatureMower.mowerLine</c>：public 字段，类型
	///     <c>Array&lt;TowerDefenseMower&gt;</c>，**下标即行号**。
	///   · <c>TowerDefenseMower.Run()</c>：public，方法体只有
	///     <c>if (!CanStartRun()) return; ActivateGameplayProcessing();
	///       SendStateEvent("ToRun");</c>
	///     ⇒ 发车走的还是原生状态机 <c>mower.run</c>（音效、加速、撞击判定全都在
	///     <c>RunEntered</c> / <c>RunProcessing</c> 里），不是伪造的假动画。
	///
	/// ★ 命中判定在**画布全局空间**里做，锚点取 <c>transformPoint</c>，
	///   **不用** <c>WorldHitRect</c>：
	///   后者来自 <c>GetGlobalTransformForShadow</c>，那个变换的基向量在第一次调用时
	///   就被冻结（IL_0036-IL_0044）、原点还会被换成物理插值缓存
	///   <c>_physicsGlobalPosition</c>（IL_0016-IL_0028）；角色没有命中框定义时
	///   更会退化成「以该原点为中心的 DefaultAreaSize 大块」
	///   （<c>get_WorldHitRect</c> IL_006b-IL_007b）。屋顶这类带偏移 / 缩放 /
	///   逐列高度曲线的关卡上，它和屏幕上看到的车完全不重合 —— 点天空都能放车。
	///   对照：原生自己的推车预览定位用的是 <c>GetMapCellPlantPos</c> +
	///   逐格 <c>GetGroundHeight</c>（<c>TowerDefenseMowerManager.GetMowerPreviewPosition</c>
	///   IL_0019 / IL_0045），是考虑了屋顶曲线的。
	///
	/// ★ 防重复触发用原生自己的标志：<c>MowerHitComponent.HitCheckCharacter</c>
	///   IL_00e7-IL_0105 是 <c>if (!parent.run) { parent.Run(); parent.run = true; }</c>
	///   ⇒ 发车瞬间 <c>run</c> 就为 true（<c>Run()</c> 自己不写，原生在外面补这一刀，
	///   这里照做）。另外车跑到底时 <c>MowerRun(mower)</c> 在 IL_002c-IL_0034 把
	///   <c>mowerLine[行号]</c> 置 null，所以「跳过 null」= 这一行已经没有车了。
	///
	/// ★ 为什么是每帧轮询、不写 <c>_Input</c> 覆写：本程序集刻意不用 Godot.NET.Sdk
	///   （见 csproj 注释），没有源码生成器就不会把方法注册成 script method，
	///   覆写 <c>_Input</c> 引擎永远不会调用。
	///   <c>Input.IsMouseButtonPressed(MouseButton)</c> 是公开静态方法，可在
	///   process_frame 里直接读；游戏里**没有任何地方**读右键
	///   （全库 <c>MouseButtonsMask</c> 引用数为 0，推车的原生输入走
	///   <c>Input.IsActionJustPressed("Press")</c>，见 <c>ProcessMowerInput</c> IL_000f），
	///   所以占用它不会抢掉任何官方操作。
	/// </summary>
	private void DriveMowerRightClick()
	{
		bool down;
		try
		{
			down = Input.IsMouseButtonPressed(MouseButton.Right);
		}
		catch
		{
			return;   // 输入状态都拿不到 ⇒ 本帧什么都不做，也不影响其它驱动
		}

		// 边沿检测：只在「松开 → 按下」那一帧往下走。
		// 不做这一步的话按住右键会一帧一台连续放车，一把就把保命车全放空。
		bool pressed = down && !_mowerRightDown;
		_mowerRightDown = down;
		if (!pressed)
		{
			return;
		}

		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			if (!mgr.IsGameRunning())
			{
				return;   // 选卡 / 结算 / 主菜单不响应
			}
			// ★ 这里刻意**不加**「鼠标停在 UI 控件上就不响应」的守卫：
			//   战场上很可能铺着一层全屏 Control，那道守卫会把整个功能一起挡死。
			//   而「必须点中推车那一格」本身就已经排除了点界面、点空地的情况。
			TowerDefenseBattleFeatureMower feature = mgr.GetMowerFeature();
			if (feature == null || !GodotObject.IsInstanceValid(feature))
			{
				return;   // 本关没有推车特性
			}
			Godot.Collections.Array<TowerDefenseMower> line = feature.mowerLine;
			if (line == null || line.Count <= 0)
			{
				return;
			}

			int best = -1;
			float bestDist = float.MaxValue;
			Vector2 bestDelta = Vector2.Zero;
			Vector2 bestHalf = Vector2.Zero;

			for (int i = 0; i < line.Count; i++)
			{
				TowerDefenseMower m = line[i];
				if (m == null || !GodotObject.IsInstanceValid(m))
				{
					continue;   // null ⇒ 这一行的车已经用掉了
				}
				// ★ 原生防撞标志：MowerHitComponent.HitCheckCharacter 在
				//   IL_00e7-IL_0105 里是 `if (!parent.run) { parent.Run(); parent.run = true; }`
				//   ⇒ 车一发车 run 就是 true（存档恢复也会写它）。
				//   用它挡掉「已经在跑 / 已经用掉的车」，和原生用的是同一个标志。
				if (m.run)
				{
					continue;
				}
				if (!m.CanStartRun())
				{
					continue;   // 游戏状态不允许发车（上面已判过，这里逐车再确认）
				}
				// ★★ 判定在**画布全局空间**里做，锚点用 transformPoint。
				//   前两版的错误：
				//     ① WorldHitRect 来自 GetGlobalTransformForShadow —— 基向量在
				//        第一次调用时就被冻结（IL_0036-IL_0044）、原点被换成物理插值
				//        缓存（IL_0016-IL_0028），屋顶上它和屏幕所见完全不重合；
				//     ② GetLocalMousePosition() 反演的是**节点自身原点**，而屋顶
				//        推车的节点原点并不在绘制位置上（车体画在偏移处）。
				//
				//   现在两边都是货真价实的画布全局坐标：
				//     · 鼠标：GetGlobalMousePosition()（视口画布全局，任何节点取都一样）；
				//     · 锚点：transformPoint.GetGlobalPosition()。
				//   transformPoint 就是游戏自己认定的视觉锚点 —— 原生
				//   TowerDefenseMower._Ready IL_00af-IL_00b5 生成推车出场特效时
				//   用的正是它（GetLogicalGlobalPosition(transformPoint)），
				//   跟着它 = 跟着玩家看到的那台车，屋顶偏移天然被包含。
				//   半格尺寸同样换算到画布全局单位（格子尺寸 × 管理器全局缩放）。
				Vector2 anchor = MowerAnchorGlobal(m);
				Vector2 gp = m.GetGlobalMousePosition();
				Vector2 half = MowerGlobalHalfCell(mgr);
				Vector2 d = gp - anchor;
				// 纯比较取绝对值（NaN 时判「没点中」，不误放车）。
				if (d.X > half.X || d.X < -half.X
					|| d.Y > half.Y || d.Y < -half.Y)
				{
					continue;
				}
				// 相邻行的框按一行一格排布，容差 1.0 时正好首尾相接、不重叠；
				// 万一有重叠，取离锚点最近的那一台，保证一次右键只放一台车。
				float dist = (d.X * d.X) + (d.Y * d.Y);
				if (dist < bestDist)
				{
					bestDist = dist;
					best = i;
					bestDelta = d;
					bestHalf = half;
				}
			}

			// 每次都打诊断（限量 5 次）：命中了也要打 —— 之前只在「没命中」时打，
			// 结果「命中了错误的那一行」这种最关键的场景反而没有数据。
			MowerMissDiag(line, best);

			if (best < 0)
			{
				return;   // 点到的不是推车 ⇒ 什么都不做
			}

			TowerDefenseMower target = line[best];
			if (target == null || !GodotObject.IsInstanceValid(target))
			{
				return;
			}
			// 与原生触发点保持完全一致：Run() 之后立刻置 run
			// （MowerHitComponent.HitCheckCharacter IL_00f9 → IL_0104 就是这个顺序）。
			// Run() 本身不会写 run，不补这一刀的话车跑起来后还能被再次点着放一遍。
			target.Run();
			target.run = true;
			Log("右键放行小推车：第 " + (best + 1) + " 行（鼠标相对锚点=("
				+ (int)bestDelta.X + ',' + (int)bestDelta.Y
				+ ") 半格=(" + (int)bestHalf.X + ',' + (int)bestHalf.Y + ")）。");
		}
		catch (Exception ex)
		{
			if (!_mowerErrLogged)
			{
				_mowerErrLogged = true;
				Log("右键放行小推车异常（本条只报一次，其余功能不受影响）：" + ex.Message);
			}
		}
	}

	/// <summary>
	/// 推车的**视觉锚点**（画布全局坐标）。
	///
	/// 首选 <c>transformPoint</c>（public 的 <c>Marker2D</c>）：原生
	/// <c>TowerDefenseMower._Ready</c> IL_00af-IL_00b5 生成推车出场特效时用的就是它，
	/// 也就是游戏自己认定的"这台车在哪儿"。
	/// 拿不到时退回节点自身的全局位置（普通草坪关卡两者本来就差得不多）。
	/// </summary>
	private static Vector2 MowerAnchorGlobal(TowerDefenseMower m)
	{
		Marker2D tp = m.transformPoint;
		if (tp != null && GodotObject.IsInstanceValid(tp))
		{
			return tp.GetGlobalPosition();
		}
		return m.GetGlobalPosition();
	}

	/// <summary>
	/// 三个**候选锚点**的画布全局坐标，用于实测对比：
	///   0 = 推车节点自身原点（第 2 版用过，屋顶上偏）
	///   1 = transformPoint（第 3 版采用：原生生成出场特效用的锚点）
	///   2 = sprite（真正画东西的那个 AdobeAnimateSprite）
	///
	/// 屋顶推车沿斜坡排列，节点原点并不落在绘制位置上，所以必须实测确认
	/// 哪一个才和玩家看到的那台车对齐 —— 不再凭推断换锚点。
	/// </summary>
	private static void MowerCandidateAnchors(
		TowerDefenseMower m, ref Vector2 a0, ref Vector2 a1, ref Vector2 a2)
	{
		a0 = m.GetGlobalPosition();
		Marker2D tp = m.transformPoint;
		a1 = (tp != null && GodotObject.IsInstanceValid(tp))
			? tp.GetGlobalPosition() : a0;
		AdobeAnimateSprite sp = m.sprite;
		a2 = (sp != null && GodotObject.IsInstanceValid(sp))
			? sp.GetGlobalPosition() : a0;
	}

	/// <summary>
	/// 「一格多大」换算到**画布全局单位**，用于右键命中判定。
	///
	/// <c>GetMapGridSize()</c> 处在管理器的局部空间里，乘管理器的全局缩放
	/// 即得屏幕上实际占多大。屋顶的战斗缩放会同时作用在鼠标与锚点两边，
	/// 这里跟着一起缩放，比例关系保持不变。
	///
	/// 任何一步算出非正常值都返回 (0,0)，调用方据此放弃本次触发 ——
	/// 宁可点不动，也不要把玩家唯一的保命车误放掉。
	/// </summary>
	private static Vector2 MowerGlobalHalfCell(TowerDefenseManager mgr)
	{
		Vector2 gridSize = mgr.GetMapGridSize();
		Vector2 mgrScale = mgr.GetGlobalScale();
		if (!SaneScale(mgrScale.X) || !SaneScale(mgrScale.Y))
		{
			return Vector2.Zero;
		}
		if (!SaneLength(gridSize.X) || !SaneLength(gridSize.Y))
		{
			return Vector2.Zero;
		}
		float hx = gridSize.X * mgrScale.X * 0.5f * MowerClickCellTolerance;
		float hy = gridSize.Y * mgrScale.Y * 0.5f * MowerClickCellTolerance;
		if (!(hx > 0f && hx < 4000f) || !(hy > 0f && hy < 4000f))
		{
			return Vector2.Zero;
		}
		return new Vector2(hx, hy);
	}

	/// <summary>
	/// 缩放因子的合理区间。用**纯比较**判有效：NaN 与任何数比较都是 false，
	/// 所以这一个条件同时挡掉 NaN、0、负数与无穷。
	/// 刻意不用 float.IsNaN / float.IsInfinity —— 游戏自身没引用过它们
	/// （全库 @ref 查无），而这类"编译通过、运行时才发现缺成员"的坑已经踩过一次。
	/// </summary>
	private static bool SaneScale(float v)
	{
		return v > 0.001f && v < 1000.0f;
	}

	/// <summary>长度（像素）的合理区间，判有效方式同 <see cref="SaneScale"/>。</summary>
	private static bool SaneLength(float v)
	{
		return v > 1.0f && v < 10000.0f;
	}

	/// <summary>
	/// 没点中任何推车时的**限量**诊断。
	///
	/// 同时打印两种候选锚点相对鼠标的偏移：
	///   T = transformPoint.GetGlobalPosition()（游戏生成推车特效用的视觉锚点，当前采用）
	///   N = 推车节点自身的 GetGlobalPosition()（旧版做法，屋顶上会偏）
	/// 半格容差也一并给出。三个数都在画布全局空间里，直接可比 ——
	/// 哪一组接近 (0,0) 就说明该用哪个锚点，一轮实测即可定案。
	///
	/// 最多报 3 条（只在真的按了右键且场上有车时才会走到这里），不会刷屏。
	/// </summary>
	private void MowerMissDiag(
		Godot.Collections.Array<TowerDefenseMower> line, int best)
	{
		if (_mowerMissLogged >= 5)
		{
			return;
		}
		_mowerMissLogged++;
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			// 用字符串拼接而不是 StringBuilder：本文件里 `+` 拼接已在游戏内验证可用，
			// 少依赖一个类型就少一分「运行时才发现缺成员」的风险。
			string detail = "";
			int available = 0;
			for (int i = 0; i < line.Count; i++)
			{
				TowerDefenseMower m = line[i];
				if (m == null || !GodotObject.IsInstanceValid(m))
				{
					continue;
				}
				if (m.run || !m.CanStartRun())
				{
					continue;
				}
				available++;
				if (available > 5)
				{
					continue;   // 一行一台，5 行足够；再多只是刷屏
				}
				Vector2 gp = m.GetGlobalMousePosition();
				Vector2 a0 = Vector2.Zero, a1 = Vector2.Zero, a2 = Vector2.Zero;
				MowerCandidateAnchors(m, ref a0, ref a1, ref a2);
				Vector2 half = MowerGlobalHalfCell(mgr);
				detail = detail + " | 第 " + (i + 1) + " 台 差节点=("
					+ (int)(gp.X - a0.X) + "," + (int)(gp.Y - a0.Y)
					+ ") 差tp=(" + (int)(gp.X - a1.X) + "," + (int)(gp.Y - a1.Y)
					+ ") 差图=(" + (int)(gp.X - a2.X) + "," + (int)(gp.Y - a2.Y)
					+ ") 半格=(" + (int)half.X + "," + (int)half.Y + ")";
			}
			Log("右键诊断（第 " + _mowerMissLogged + " 次，本次命中第 "
				+ (best < 0 ? "-（未命中）" : (best + 1).ToString())
				+ " 行）：可用 " + available + " 台" + detail);
		}
		catch
		{
			// 诊断本身失败不影响玩法。
		}
	}

	// ================================================================ 钻卡页签注入

	/// <summary>
	/// 把本卡追加进「钻卡」页签的分类表，让它同时出现在「Mod 植物」和「钻卡」两处。
	///
	/// 为什么要运行时注入，而不是靠 <c>type = DIAMOND</c> 自动生效：
	///   页签归属与 <c>type</c> 是两套独立机制。
	///     · <c>type</c> 只管卡面边框（<c>UpdateBackgroundTexture</c> 的分支）与
	///       改价免疫（<c>TowerDefensePacketChangeCost.ModifyCost</c> 按
	///       <c>amontDictionary[packet.type]</c> 查表，IL_0054 ContainsKey 不中即 ret）。
	///     · 页签是 <c>TowerDefensePacketBankData.category</c>（public
	///       <c>Godot.Collections.Dictionary</c>）的键，选卡界面用
	///       <c>GetCategory(string)</c> 纯按键查表（IL_000c ContainsKey → IL_001f get_Item）。
	///   而 Mod 卡被游戏硬编码塞进 <c>"ModPlants"</c> 这一个键 ——
	///   <c>XWModContentCatalog.WithPlants</c> IL_0062-IL_0077：
	///     <c>category["ModPlants"] = 本 Mod 提供的全部 Packet</c>。
	///   清单里没有任何字段能改这个键名，所以只能运行时往 <c>"Diamond"</c> 追加。
	///
	/// 注入目标必须是 <c>feature.packetBankData</c>（public 字段），不是
	/// <c>TowerDefenseManager.GetPacketBankData(name)</c> 的返回值：后者虽然返回注册表里的
	/// 共享实例（IL_0135 直接 ret，无 Duplicate），但 <c>SetPacketBankData</c> 在
	/// <c>packetBankType == "GeneralPlant"</c> 时会走 <c>WithPlants</c>，而
	/// <c>WithPlants</c> 开头就 <c>category.Duplicate(true)</c> 造了**新副本**再赋值给
	/// <c>packetBankData</c>（IL_0007-IL_0012）。改注册表那份会被这次深拷贝丢掉，
	/// 所以只能改 feature 上正在用的这一份。
	///
	/// 安全性（逐条核对过 IL）：
	///   · <c>RebuildPacketAvailabilityIndex</c> 遍历 <c>category.Values</c> 全部数组塞进
	///     HashSet ⇒ 同一个 key 出现在两个分类里，集合内容不变，无需重建索引。
	///   · <c>GetUnlockPacket</c> 也遍历所有分类，但只对 <c>Unlock()</c> 为 true 的卡追加；
	///     本卡 <c>unlockCheckList = []</c>，<c>Unlock</c> 在 IL_005b-IL_0062 直接
	///     <c>ldc.i4.0; ret</c> ⇒ 不会被重复计入「已解锁卡」列表。
	///   · 页签按钮 <c>CardMod</c> 的可见性读的是 <c>category.ContainsKey("ModPlants")</c>
	///     （<c>SetPacketBankData</c> IL_009b-IL_00af），我们只追加、不移除 ⇒ 不受影响。
	///   · 「钻卡」页签的按钮本身在选卡场景里预置（<c>category = "Diamond"</c>），
	///     关卡若没有该键，<c>GetCategory</c> 返回空数组 ⇒ 这里用 ContainsKey 兜住，
	///     没有该分类就什么都不做，绝不新建键（免得凭空多出一个空页签）。
	///
	/// 幂等：每次先 <c>Contains</c> 判断，绝不重复追加。
	/// 换关判定用 <b>packetBankData 自己的实例 id</b>（就是被我们改的那个对象）：
	///   <c>SetPacketBankData</c> 会把 <c>packetBankData</c> 整体重新赋值成
	///   <c>WithPlants</c> 的深拷贝，实例 id 必然变 ⇒ 新对象会再注入一次。
	///   若改用 feature 的实例 id，同一关内二次初始化会被误判成「已注入」而漏掉。
	/// </summary>
	private void DriveDiamondTab()
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			TowerDefenseBattleFeaturePacketBank feature = mgr.GetPacketBankFeature();
			if (feature == null || !GodotObject.IsInstanceValid(feature))
			{
				return;
			}

			TowerDefensePacketBankData data = feature.packetBankData;
			if (data == null || !GodotObject.IsInstanceValid(data))
			{
				return;   // 还没进选卡界面，下一帧再来
			}
			// 这个分类表对象已经处理过 ⇒ 什么都不做（每帧只付两次取实例的代价）。
			ulong did = data.GetInstanceId();
			if (did == _diamondInjectedFor)
			{
				return;
			}

			Godot.Collections.Dictionary category = data.category;
			if (category == null)
			{
				return;
			}
			// 本关卡池里没有「钻卡」这一类 ⇒ 不动（不新建键，避免凭空多出空页签）。
			if (!category.ContainsKey(DiamondCategory))
			{
				_diamondInjectedFor = did;
				return;
			}

			Godot.Collections.Array list = (Godot.Collections.Array)category[DiamondCategory];
			if (list == null)
			{
				_diamondInjectedFor = did;
				return;
			}
			// 用隐式转换而不是 new Variant(...)：GodotSharp 提供的是
			// op_Implicit(string) -> Variant，构造函数重载是否存在不值得赌。
			Variant key = MyKey;
			if (!list.Contains(key))
			{
				list.Add(key);
				if (!_diamondLogged)
				{
					_diamondLogged = true;
					Log("已把本卡追加进「" + DiamondCategory + "」页签（钻卡分类）。");
				}
			}
			_diamondInjectedFor = did;
		}
		catch (Exception ex)
		{
			if (!_diamondErrLogged)
			{
				_diamondErrLogged = true;
				Log("钻卡页签注入失败（本条只报一次，不影响其它功能）：" + ex.Message);
			}
		}
	}

	// ================================================================ 编辑器卡牌库

	/// <summary>
	/// 把本卡追加进**自制关卡编辑器**的「卡牌库」（钻卡页签）。
	///
	/// ── 为什么关卡里能看到、编辑器里看不到 ──
	/// 两处读的是**不同**的卡池对象：
	///   · 战斗选卡界面读关卡特征自带的 <c>feature.packetBankData</c>
	///     （<see cref="DriveDiamondTab"/> 注的就是它）；
	///   · 编辑器的三个卡牌库面板（<c>LevelEditorPacketBank</c> /
	///     <c>LevelEditorSeedBankChoose</c> / <c>LevelEditorWaveEditorPacketChoose</c>）
	///     读的是**全局总卡池** <c>TOWERDEFENSE_PACKETBANKS["Total"]</c>：
	///       - <c>LevelEditorPacketBank.TryLoadDefaultPacketBank()</c>
	///         IL_001f <c>get_TOWERDEFENSE_PACKETBANKS</c> → IL_0024 <c>ldstr "Total"</c>
	///         → <c>TryGetValue</c> → <c>stfld data</c>；
	///       - <c>LevelEditorSeedBankChoose._Ready()</c> IL_00bd <c>ldstr "Total"</c>
	///         → <c>GetPacketBankData("Total")</c> 塞进 <c>data</c>。
	///     而 <c>GetPacketBankData</c> 是 IL_0135 直接 <c>ret</c> 注册表里的**共享实例**
	///     （没有 Duplicate）⇒ 改这一份就是改它们看到的这一份。
	/// Mod 的卡由 <c>XWModContentCatalog.WithPlants</c> 归到 <c>"ModPlants"</c> 键，
	/// 总卡池里根本没有这个键 ⇒ 编辑器卡牌库一个 Mod 卡都不显示。
	///
	/// ── 做法 ──
	/// 往总卡池 <c>category["Diamond"]</c>（本卡是钻卡，页签按钮
	/// <c>PacketCategoryButton.category = "Diamond"</c>，与关卡内一致）追加本卡 key；
	/// 该键不存在时新建一条数组（页签按钮是场景里固定存在的，点了才有列表）。
	/// ★ 总卡池是全局共享的 ⇒ 这一改动对**编辑器**是持久的（不像关卡那份每关重建）。
	///   只追加自己这一张、只加一次，不删不改别人的条目。
	/// ★ 全游戏读 "Total" 的只有那三个编辑器面板加一个调试开关
	///   （<c>CommandManager.debugPacketOpenAll</c>，它本来就"全开"），
	///   战斗选卡、图鉴、随机变身组件等都不用它 ⇒ 不会影响正常关卡。
	/// ★ 逐帧 + 按实例 id 去重（同 <see cref="DriveDiamondTab"/>）：Mod 加载时
	///   总卡池可能还没建好，编辑器也可能反复重建它。
	/// </summary>
	private void DriveEditorCardBank()
	{
		try
		{
			ResourceManager rm = ResourceManager.Instance;
			if (rm == null || !GodotObject.IsInstanceValid(rm))
			{
				return;
			}
			var banks = rm.TOWERDEFENSE_PACKETBANKS;
			if (banks == null)
			{
				return;
			}
			TowerDefensePacketBankData total;
			if (!banks.TryGetValue(EditorTotalBank, out total))
			{
				return;   // 总卡池还没建好，下一帧再来
			}
			if (total == null || !GodotObject.IsInstanceValid(total))
			{
				return;
			}
			Godot.Collections.Dictionary category = total.category;
			if (category == null)
			{
				return;
			}
			ulong did = total.GetInstanceId();
			if (did == _editorInjectedFor && _editorInjectedOk)
			{
				return;   // 这一份已经注过了
			}
			Variant key = MyKey;
			Godot.Collections.Array list;
			if (category.ContainsKey(DiamondCategory))
			{
				// 与 DriveDiamondTab 一致：Variant → Array 用显式转换（GodotSharp 提供
				// explicit operator），`as` 对 Variant 这个结构体不成立。
				list = (Godot.Collections.Array)category[DiamondCategory];
				if (list == null)
				{
					_editorInjectedFor = did;
					return;   // 这个键不是数组（结构异常）⇒ 不动
				}
			}
			else
			{
				list = new Godot.Collections.Array();
				category[DiamondCategory] = list;
			}
			if (!list.Contains(key))
			{
				list.Add(key);
			}
			_editorInjectedFor = did;
			_editorInjectedOk = true;
			if (!_editorLogged)
			{
				_editorLogged = true;
				Log("已把本卡加入自制关卡编辑器的卡牌库（「" + DiamondCategory
					+ "」= 钻卡页签）：编辑器的卡牌库读的是全局总卡池，"
					+ "与关卡选卡界面用的那份不是同一个对象。");
			}
		}
		catch (Exception ex)
		{
			if (!_editorErrLogged)
			{
				_editorErrLogged = true;
				Log("编辑器卡牌库注入失败（本条只报一次，不影响其它功能）：" + ex.Message);
			}
		}
	}

	/// <summary>全局总卡池的键（编辑器卡牌库的数据源）。</summary>
	private const string EditorTotalBank = "Total";

	/// <summary>已注入过的总卡池实例 id。</summary>
	private ulong _editorInjectedFor;

	/// <summary>这一次注入是否已经完成（完成后同一实例不再重复动手）。</summary>
	private bool _editorInjectedOk;

	/// <summary>编辑器卡牌库注入成功的日志是否已报过。</summary>
	private bool _editorLogged;

	/// <summary>编辑器卡牌库注入失败的日志是否已报过。</summary>
	private bool _editorErrLogged;

	// ================================================================ 注册混合卡池

	/// <summary>
	/// 往 `ResourceManager.TOWERDEFENSE_PACKETBANKS` 注册 `RandomImitaterMixed`：
	/// `White` = 全部植物 + 全部僵尸（`Explode()` 抽的就是 `White`），`Original` = 空。
	/// </summary>
	private bool EnsureMixedBank()
	{
		try
		{
			ResourceManager rm = ResourceManager.Instance;
			if (rm == null || !GodotObject.IsInstanceValid(rm))
			{
				return false;
			}
			var banks = rm.TOWERDEFENSE_PACKETBANKS;
			if (banks == null)
			{
				return false;
			}
			if (banks.ContainsKey(CustomBank))
			{
				// ★ 不再直接 return true：设置变化时本方法要**用新参数重建**同一个键。
				//   重建是幂等的（整份替换字典条目），且只在 _bankOk=false 时被调用，
				//   所以正常运行时每关最多进来一次，不会反复重建。
				if (_rebuildCount > 0)
				{
					Log("重建混合卡池（应用新设置）…");
				}
			}

			var plantList = new Godot.Collections.Array();
			var zombieList = new Godot.Collections.Array();
			var seen = new HashSet<string>(StringComparer.Ordinal);
			int skipped = 0;

			// ① 首选：具名卡池（和以前一样，保证卡池构成、概率与今天完全一致）。
			//    类别不再写死：原生 `GetPlantList()`（IL_0017 起）认的是
			//    White/Gold/Diamond/Colour/Star/Original 这六类，直接按名字逐个取；
			//    这里额外**遍历该卡池的全部类别键**（非植物条目由 Collect 自己过滤掉），
			//    这样官方新增/改名某一类（例如把进阶卡单列一类）也不会漏收。
			TowerDefensePacketBankData plantSrc = TowerDefenseManager.GetPacketBankData(PlantSrcBank);
			if (plantSrc != null && GodotObject.IsInstanceValid(plantSrc))
			{
				foreach (string cat in new[] { "White", "Gold", "Diamond", "Colour", "Star", "Original" })
				{
					Collect(plantSrc.GetCategory(cat), plantList, seen, wantZombie: false, ref skipped);
				}
				foreach (Variant k in PlantCategoryKeys(plantSrc))
				{
					Collect(plantSrc.GetCategory(k.AsString()), plantList, seen,
						wantZombie: false, ref skipped);
				}
			}

			TowerDefensePacketBankData zombieSrc = TowerDefenseManager.GetPacketBankData(ZombieSrcBank);
			if (zombieSrc != null && GodotObject.IsInstanceValid(zombieSrc))
			{
				Collect(zombieSrc.GetCategory(ZombieCat), zombieList, seen, wantZombie: true, ref skipped);
			}

			// ② 兜底：具名池拿不到（官方改名/重组）时，**遍历全部卡池**按类型能力收集。
			//    这样版本更新导致卡池改名也不会「只能开出僵尸」，Mod 能自愈。
			if (plantList.Count == 0 || zombieList.Count == 0)
			{
				Log("具名卡池不全（植物 " + plantList.Count + " / 僵尸 " + zombieList.Count
					+ "），改用遍历全部卡池兜底。");
				ScanAllBanks(plantList, zombieList, seen, ref skipped);
			}

			if (plantList.Count == 0) { Log("拿不到植物卡池 " + PlantSrcBank + "。"); }
			if (zombieList.Count == 0) { Log("拿不到僵尸卡池 " + ZombieSrcBank + "。"); }

			int repP, repZ;
			Godot.Collections.Array mixed = BuildWeighted(plantList, zombieList, _plantChance,
				out repP, out repZ);
			if (mixed.Count == 0)
			{
				Log("混合卡池为空，随机开出暂不生效。");
				return false;
			}

			TowerDefensePacketBankData data = new TowerDefensePacketBankData();
			data.category["White"] = mixed;                              // Explode() 抽的就是 White
			data.category["Original"] = new Godot.Collections.Array();   // 留空：不回落到原版卡
			banks[CustomBank] = data;

			// 记下这一版卡池里的植物 key：`RiExplode` 要按这份名单做「地形临时放宽」
			// （见 RandomImitater.WidenPoolPlants 的注释）。必须在重建成功后同步更新，
			// 否则玩家改了概率导致卡池重建后，放宽的还是上一版的植物。
			PoolPlantKeys.Clear();
			foreach (Variant v in plantList)
			{
				string k = v.AsString();
				if (!string.IsNullOrEmpty(k))
				{
					PoolPlantKeys.Add(k);
				}
			}

			// 实际比例按**列表内容**数出来，不用 repP/repZ 推算：
			// 0% 与 100% 两个极端下 BuildWeighted 直接返回单边列表（此时 rep 仍是 1/1，
			// 按公式推算会得出「335/302 = 110.9%」这种荒谬数字）。
			int plantEntries = 0;
			for (int i = 0; i < mixed.Count; i++)
			{
				if (PoolPlantKeys.Contains(mixed[i].AsString()))
				{
					plantEntries++;
				}
			}
			double realChance = mixed.Count > 0 ? (double)plantEntries / mixed.Count : 0.0;
			int coverPool = RandomImitater.CountCoverPoolPlants();
			if (!_coverPoolReported)
			{
				_coverPoolReported = true;
				if (coverPool > 0)
				{
					Log("卡池里有 " + coverPool
						+ " 张「必须种在别的植物上」的覆盖型（组合/进阶，即紫卡）——"
						+ "变身时这道限制会临时放开，所以它们也抽得到。");
				}
				else
				{
					Log("卡池里没有带「必须种在别的植物上」限制的植物卡（覆盖型/紫卡 0 张）"
						+ "⇒ 本 Mod 不需要为它们放开什么；若仍抽不到某张卡，原因在别处。");
				}
			}
			Log("已注册卡池 " + CustomBank + "：植物 " + plantList.Count + " 张 + 僵尸 "
				+ zombieList.Count + " 只；每张植物 x" + repP + "、每只僵尸 x" + repZ
				+ " ⇒ 列表 " + mixed.Count + " 条，实际出植物概率 "
				+ (realChance * 100).ToString("0.###") + "%（目标 "
				+ (_plantChance * 100).ToString("0.#") + "%）；同类卡等概率；跳过 " + skipped + "；"
				+ "BOSS " + (AllowBossZombie ? "已含" : "已排除")
				+ "、巨人 " + (AllowGargantuar ? "已含" : "已排除") + "。");
			return true;
		}
		catch (Exception ex)
		{
			Log("注册混合卡池失败（已吞）：" + ex.Message);
			return false;
		}
	}

	/// <summary>
	/// 兜底用：不看卡池名字，遍历 <c>ResourceManager.TOWERDEFENSE_PACKETBANKS</c> 里的每个卡池，
	/// 用引擎自己的 <c>GetPlantList()</c> / <c>GetZombieList()</c> 取条目
	/// （它们内部按类型分类名汇总，官方重组卡池时也照样有效），
	/// 再交给 <c>Collect</c> 逐条解析配置类型过滤。
	///
	/// 只在具名卡池取不到时调用 —— 因为并集比 <c>GeneralPlant</c> 多约 20 张特殊卡
	/// （保龄球/种子/烟花一类小游戏卡），默认路径不引入它们，避免改变现有概率手感。
	/// </summary>
	private void ScanAllBanks(
		Godot.Collections.Array plantList, Godot.Collections.Array zombieList,
		HashSet<string> seen, ref int skipped)
	{
		try
		{
			ResourceManager rm = ResourceManager.Instance;
			if (rm == null || !GodotObject.IsInstanceValid(rm) || rm.TOWERDEFENSE_PACKETBANKS == null)
			{
				return;
			}
			foreach (var kv in rm.TOWERDEFENSE_PACKETBANKS)
			{
				if (string.Equals(kv.Key, CustomBank, StringComparison.Ordinal))
				{
					continue;   // 跳过自己，防止自我膨胀
				}
				TowerDefensePacketBankData b = kv.Value;
				if (b == null || !GodotObject.IsInstanceValid(b))
				{
					continue;
				}
				Collect(TryCatList(() => b.GetPlantList()), plantList, seen, wantZombie: false, ref skipped);
				Collect(TryCatList(() => b.GetZombieList()), zombieList, seen, wantZombie: true, ref skipped);
			}
		}
		catch (Exception ex)
		{
			Log("遍历卡池异常（已吞）：" + ex.Message);
		}
	}

	/// <summary>取分类列表，失败当作空 —— 兜底路径不能因为某个卡池抛错就整体失效。</summary>
	private static Godot.Collections.Array TryCatList(Func<Godot.Collections.Array> f)
	{
		try
		{
			return f();
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// 按 <paramref name="plantChance"/> 把植物表与僵尸表合成一个抽取列表。
	///
	/// 游戏那边是 `array.PickRandom()`（**等概率**取下标），所以「出植物的概率」只能靠
	/// 调整列表构成来实现。关键约束：**同类卡在列表里出现的次数必须完全相同**，
	/// 否则出现次数多的卡中奖率就更高。
	///
	/// 做法：每张植物放 <c>a</c> 次、每只僵尸放 <c>b</c> 次（a、b 为常数），
	/// 于是
	///     出植物概率 = p·a / (p·a + z·b)
	/// 在 a、b ≤ 上限范围内暴力搜索，取**误差最小、列表最短**的那组。
	///
	/// ⚠️ 早期版本用「循环取模填充到目标条数」，会按列表顺序给前 N 张卡翻倍 ——
	/// 而列表顺序是固定的分类顺序（White→Gold→…），结果是**白卡、金卡被双倍加权**，
	/// 彩卡/星卡/原版卡被砍掉约 40%。这与「完全随机」相悖，已废弃。
	///
	/// 实测（植物 325 / 僵尸 290、出植物概率 = 0.65）：最优解 a=58、b=35，
	/// 植物条目 18850 + 僵尸条目 10150 = 29000，出植物概率**正好 65.0000%**，
	/// 且每张植物、每只僵尸的中奖率各自完全相同。
	///
	/// ✅ 与 `plantGridType` 怎么填无关：`RiExplode()` 先把自己腾走，
	/// 原生落点校验看到空地，植物照常种得下（旧版留 `[-1]` 的那个坑见 RandomImitater 类注释）。
	/// </summary>
	private static Godot.Collections.Array BuildWeighted(
		Godot.Collections.Array plants, Godot.Collections.Array zombies, double plantChance)
	{
		return BuildWeighted(plants, zombies, plantChance, out _, out _);
	}

	/// <summary>
	/// <see cref="BuildWeighted(Godot.Collections.Array, Godot.Collections.Array, double)"/> 的实现，
	/// 额外回传选中的重复次数 <paramref name="aOut"/>（每张植物）与 <paramref name="bOut"/>（每只僵尸），
	/// 供日志如实打印实际比例。
	/// </summary>
	private static Godot.Collections.Array BuildWeighted(
		Godot.Collections.Array plants, Godot.Collections.Array zombies, double plantChance,
		out int aOut, out int bOut)
	{
		var result = new Godot.Collections.Array();
		int p = plants.Count;
		int z = zombies.Count;

		aOut = 1;
		bOut = 1;

		if (p == 0 && z == 0)
		{
			return result;
		}
		if (p == 0) { return new Godot.Collections.Array(zombies); }
		if (z == 0) { return new Godot.Collections.Array(plants); }

		if (plantChance < 0) { plantChance = 0; }
		if (plantChance > 1) { plantChance = 1; }
		if (plantChance >= 1.0) { return new Godot.Collections.Array(plants); }
		if (plantChance <= 0.0) { return new Godot.Collections.Array(zombies); }

		// 搜索「每张植物 a 次 / 每只僵尸 b 次」：误差最小优先，其次列表最短。
		int aMax = Math.Max(1, MixedListMaxLen / p);
		int bMax = Math.Max(1, MixedListMaxLen / z);
		int bestA = 1, bestB = 1, bestLen = p + z;
		double bestErr = double.MaxValue;

		for (int a = 1; a <= aMax; a++)
		{
			for (int b = 1; b <= bMax; b++)
			{
				int pe = p * a;
				int ze = z * b;
				int len = pe + ze;
				if (len > MixedListMaxLen)
				{
					break;              // b 再大只会更长
				}
				double err = Math.Abs((double)pe / len - plantChance);
				if (err < bestErr - 1e-12 || (Math.Abs(err - bestErr) <= 1e-12 && len < bestLen))
				{
					bestErr = err;
					bestA = a;
					bestB = b;
					bestLen = len;
				}
			}
		}

		aOut = bestA;
		bOut = bestB;

		// 填充：同类卡次数完全一致 ⇒ 组内严格等概率，与分类、顺序都无关。
		for (int i = 0; i < p; i++)
		{
			for (int k = 0; k < bestA; k++)
			{
				result.Add(plants[i]);
			}
		}
		for (int i = 0; i < z; i++)
		{
			for (int k = 0; k < bestB; k++)
			{
				result.Add(zombies[i]);
			}
		}
		return result;
	}

	/// <summary>
	/// 把一组 packet key 收进目标列表：只收类型匹配（植物 / 僵尸）且能解析出配置的卡。
	/// </summary>
	/// <summary>
	/// 取一个卡池对象的**全部**类别键（去重后的字符串列表）。
	/// 用于「不只按写死的六个类别收卡」：官方另立类别时也不会漏收。
	/// ★ 只读，不改任何东西；<c>category</c> 为公开字段（原生
	///   <c>GetPlantList()</c> 内部就是遍历它的键）。
	/// </summary>
	private static List<string> PlantCategoryKeys(TowerDefensePacketBankData data)
	{
		var keys = new List<string>();
		try
		{
			Godot.Collections.Dictionary category = data.category;
			if (category == null)
			{
				return keys;
			}
			foreach (Variant k in category.Keys)
			{
				string s = k.AsString();
				if (!string.IsNullOrEmpty(s) && !keys.Contains(s))
				{
					keys.Add(s);
				}
			}
		}
		catch
		{
			// 静默：拿不到就只按写死的那六类收。
		}
		return keys;
	}

	private void Collect(Godot.Collections.Array src, Godot.Collections.Array dst,
		HashSet<string> seen, bool wantZombie, ref int skipped)
	{
		try
		{
			if (src == null)
			{
				return;
			}
			foreach (Variant v in src)
			{
				string key = v.AsString();
				if (string.IsNullOrEmpty(key) || seen.Contains(key) || IsExcluded(key))
				{
					continue;
				}
				TowerDefensePacketConfig cfg;
				try
				{
					cfg = TowerDefenseManager.GetPacketConfig(key);
				}
				catch
				{
					skipped++;   // 卡池里混着非卡牌条目（道具/墓碑/割草机等）
					continue;
				}
				if (cfg == null || !GodotObject.IsInstanceValid(cfg) || cfg.characterConfig == null)
				{
					skipped++;
					continue;
				}

				bool isPlant = cfg.characterConfig is TowerDefensePlantConfig;
				bool isZombie = cfg.characterConfig is TowerDefenseZombieConfig;
				if (wantZombie ? !isZombie : !isPlant)
				{
					skipped++;
					continue;
				}
				if (isZombie && !IsZombieAllowed(key))
				{
					skipped++;
					continue;
				}

				seen.Add(key);
				dst.Add(key);
			}
		}
		catch (Exception ex)
		{
			Log("收集卡池条目异常（已吞）：" + ex.Message);
		}
	}

	/// <summary>
	/// 按开关过滤僵尸：BOSS / 巨人系列可分别关掉。
	/// ★ 现在两项都是常量 true（始终允许），所以本方法恒返回 true；
	///   保留这个结构是为了以后需要时能一处改回。
	/// </summary>
	private static bool IsZombieAllowed(string key)
	{
		if (!AllowBossZombie && key.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return false;
		}
		if (!AllowGargantuar && key.IndexOf("Gargantuar", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return false;
		}
		return true;
	}

	private static bool IsExcluded(string key)
	{
		for (int i = 0; i < ExcludeKeys.Length; i++)
		{
			if (string.Equals(key, ExcludeKeys[i], StringComparison.Ordinal))
			{
				return true;
			}
		}
		return false;
	}

	// ================================================================ 工具

	/// <summary>
	/// 反射读字段（含非 public；找不到返回 null）。
	/// 逐级遍历基类：C# 的 `Type.GetField(name, NonPublic|Instance)`
	/// **不查基类自己声明的 private 字段**。
	/// </summary>
	private static object GetMember(object target, string name)
	{
		if (target == null)
		{
			return null;
		}
		try
		{
			for (Type t = target.GetType(); t != null; t = t.BaseType)
			{
				FieldInfo f = t.GetField(name,
					BindingFlags.Public | BindingFlags.NonPublic |
					BindingFlags.Instance | BindingFlags.DeclaredOnly);
				if (f != null)
				{
					return f.GetValue(target);
				}
			}
		}
		catch { }
		return null;
	}

	/// <summary>反射写字段（含非 public；逐级遍历基类）。</summary>
	private static bool SetMember(object target, string name, object value)
	{
		if (target == null)
		{
			return false;
		}
		try
		{
			for (Type t = target.GetType(); t != null; t = t.BaseType)
			{
				FieldInfo f = t.GetField(name,
					BindingFlags.Public | BindingFlags.NonPublic |
					BindingFlags.Instance | BindingFlags.DeclaredOnly);
				if (f != null)
				{
					f.SetValue(target, value);
					return true;
				}
			}
		}
		catch { }
		return false;
	}

	/// <summary>
	/// 把 <paramref name="fieldName"/> 这个「字段式事件」整体替换成
	/// <paramref name="handler"/> 的委托（委托类型取自字段自身类型，无需在编译期知道它）。
	/// </summary>
	private bool ReplaceEventField(object target, string fieldName, MethodInfo handler)
	{
		if (target == null || handler == null)
		{
			return false;
		}
		try
		{
			for (Type t = target.GetType(); t != null; t = t.BaseType)
			{
				FieldInfo f = t.GetField(fieldName,
					BindingFlags.Public | BindingFlags.NonPublic |
					BindingFlags.Instance | BindingFlags.DeclaredOnly);
				if (f == null)
				{
					continue;
				}
				Delegate d = Delegate.CreateDelegate(f.FieldType, this, handler);
				f.SetValue(target, d);
				return true;
			}
		}
		catch { }
		return false;
	}

	/// <summary>读某个字段式事件当前挂的委托（用于判断是否还是我们的处理器）。</summary>
	private static object GetEventField(object target, string fieldName)
	{
		return GetMember(target, fieldName);
	}

	/// <summary>
	/// 往 <paramref name="fieldName"/> 这个「字段式事件」**追加**一个处理器
	/// （保留游戏已挂的，我们的排在后面 ⇒ 原生逻辑先跑）。
	/// 与游戏自己的 `NinePatchButtonBase.add_OnPressed` 等价：
	///   IL_000B Delegate.Combine(现有, 新增) → castclass → IL_001C Interlocked.CompareExchange
	/// 委托类型取自字段自身类型，编译期不需要知道那个嵌套类型。
	/// </summary>
	private bool AppendEventField(object target, string fieldName, MethodInfo handler)
	{
		if (target == null || handler == null)
		{
			return false;
		}
		try
		{
			for (Type t = target.GetType(); t != null; t = t.BaseType)
			{
				FieldInfo f = t.GetField(fieldName,
					BindingFlags.Public | BindingFlags.NonPublic |
					BindingFlags.Instance | BindingFlags.DeclaredOnly);
				if (f == null)
				{
					continue;
				}
				Delegate d = Delegate.CreateDelegate(f.FieldType, this, handler);
				f.SetValue(target, Delegate.Combine(f.GetValue(target) as Delegate, d));
				return true;
			}
		}
		catch { }
		return false;
	}

	private void Swallow(string where, Exception ex)	{
		try { GD.PrintErr(P + where + " 异常（已吞）：" + ex.Message); } catch { }
	}

	private void Log(string msg)
	{
		if (!EnableLog)
		{
			return;
		}
		try { GD.Print(P + msg); } catch { }
	}
}

// ================================================================ 伴随脚本（CompanionOnly）
//
// 引擎通过反射创建本类（`XWModCharacterCompanionRuntime.TryCreateInstance()`）：
//   1. 实例化包内角色场景 → authoredRoot
//   2. 读元数据 mod_character_script_binding = "CompanionOnly" + mod_character_script_path
//   3. expectedTypeName = Path.GetFileNameWithoutExtension(path) → "RandomImitater"
//   4. 在 Runtime/ModAssembly.dll 里找 !IsAbstract && Name == "RandomImitater"
//      && authoredRoot.GetType().IsAssignableFrom(type) 的类型
//   5. Activator.CreateInstance 反射建实例，把壳的属性 + 子节点搬过去
//
// ⚠️ 必须继承 `TowerDefensePlantImitater` —— 包内角色场景 instance 的正是
//    `TowerDefensePlantImitater.tscn`，`authoredRoot.GetType()` 就是它；
//    继承别的类会判「基类不兼容」。
// ⚠️ 没有这个类 ⇒ CreateCharacter 失败并静默回退内置模仿者
//    （表现为行为变成内置模仿者的复制上一次选择，而不是随机）。
//
// ---------------------------------------------------------------- 为什么要重写变身
//
// 官方模仿者（`TowerDefensePlantImitater`）的配置是 `plantGridType = [-1]`（ALL），
// 后果（IL 证据：`CharacterPlant` 的占格登记循环 + `CanPacketPlant` 的 IL_015B 早退）：
//   · `[-1]` ⇒ `config.plantGridType.Contains(2)` 恒为 **false** ⇒ 模仿者**根本不占任何槽位**
//     ⇒ ① 想叠多少叠、南瓜/花盆里有植物也照种（`CanPacketPlant` 在 IL_015B 直接 return true）
//     （「僵尸不吃它」跟占格**无关**，那是另一道闸门 —— 见下面 `TickBiteable` 的注释）
//   · 收窄成普通植物的 [2,6,5] 确实能占格、被咬、禁叠种，但**旧版会 100% 出僵尸**：
//     `Explode()` 校验植物用的是**模仿者自己那格**（IL_00DB `ldarg.0.cell` → IL_00E5
//     `CanPacketPlant`），而那时本体还占着槽位 ⇒ 植物一律判 false ⇒ IL_00EC `Remove` 掉
//     这条植物重抽，僵尸分支不校验 ⇒ 循环必然落到僵尸才退出。
//
// 游戏自己在第 7 章用 `TowerDefensePlantImitaterW` 修掉了这个问题，做法极简单：
//
//     IL_0009:  Destroy(false)      ← Explode() 的**第一行**，先把自己从格子上腾走
//     IL_0122:  this.cell.CanPacketPlant(cfg, false, false)   ← 再校验（此时格子已空）
//     IL_014E:  cfg.Plant(this.gridPos, true, false, default, false, false)
//     IL_03C3:  CharacterUnregister(this) + RemoveFromGroup("Character") + QueueFree()
//
// 「`Destroy(false)` 能否**同步**腾空槽位」是这套做法成立与否的唯一关键，已逐条查清：
//     Destroy(bool) → DestroyComponent.DestroyAsync(false, ·)
//       IL_0083  NotifyOwnerBeforeDestroy
//       IL_0089  MarkDestroyState          （isDestroy/die = true）
//       IL_008F  HitBoxDestroy
//       IL_0111  EmitDestroy()             ← callvirt DestroyEventHandler.Invoke()
//                … 而 `cell.CharacterDestroy` 正是 `CharacterPlant` 里
//                  `add_OnDestroy(new DestroyEventHandler(cell.CharacterDestroy))` 挂上去的
//       IL_0188/0277 起才有第一个 await
//     而 `TowerDefenseCellInstance.CharacterDestroy` 内部
//       IL_017F-0188  `slot[g] = null`
//       IL_0118-0120  `characterSlotDictionary[k] = null`
//       IL_01AD       `characterList.Remove(this)`
//     ⇒ **全部同步**、发生在第一个 await 之前 ⇒ `Destroy(false)` 返回时格子已经空了。
//     另外 `ExplodeExited` 只在 `craterCreateUse` 分支才把 `cell` 置 null（IL_056C），
//     且 `InvokeExplodeCallbacks()` 在 IL_0572 是**最后**一步 ⇒ 进 `Explode()` 时 `this.cell` 有效。
//
// 于是本 Mod 采用官方同款顺序，自定义 `Explode`，从而**同时**满足：
//   不许叠种模仿者 / 花盆·睡莲·南瓜壳·咖啡豆里必须有植物才放得下（即「和普通植物一致」）/
//   墓碑·弹坑不能种 / 僵尸会来吃它。
//
// ---------------------------------------------------------------- 挂载方式
// `Explode()` 是 **public 但非 virtual**（实测 IsVirtual=False），所以「重写」它没用 ——
// 组件绑定的是 `ExplodeComponent.OnExplode` 这个**字段式事件**（IL_000D 在调用时才 `ldfld`
// 读字段，所以整体替换该字段即可生效）。`_Ready()` 则是 **public virtual**（实测 IsVirtual=True），
// 官方 `ImitaterW` 也正是 `override _Ready` + 自己绑事件。这里 `base._Ready()` 之后把
// `OnExplode` 字段换成我们自己的处理器（与接管卡片 `OnPressed` 同一套反射手法）。
public partial class RandomImitater : TowerDefensePlantImitater
{
	/// <summary>变身事件当前挂的是不是我们自己的处理器（幂等判断用）。</summary>
	private bool _riHooked;

	/// <summary>
	/// ★ 是否让「模仿者本体」在旋转期间也能被普通僵尸啃咬。
	///
	/// 引擎默认是**咬不到**的：`ExplodeComponent.ProtectsFromBites` 在
	/// 状态机处于 `explode.explode`（就是那个旋转）期间直接返回 true ⇒
	/// `AttackComponent.IsBiteImmune` 判它免疫 ⇒ 普通僵尸从旁边走过去；
	/// 而巨人砸走的是 `ApplyCellSmashHurt` / `ResolveCellTarget`（按格子找目标，
	/// 不看这个闸门）⇒ 表现就是「只有巨人会砸它」。**官方模仿者本来就是这样。**
	///
	/// 引擎自己留了开关：`ProtectsFromBites` 返回的是 `izmMode ? isHurt : true`，
	/// 所以 `izmMode = true` + `isHurt = false` 时它就返回 false ⇒ 可被咬，
	/// 并且 `ExplodeEntered` 在这组取值下**不会**再调用 `EnableExplodeInvincibility()`
	/// 把 `instance.invincible` 打开；`checkIZM = false` 让 `ExplodeProcessing` 走
	/// 正常 `timeScale` 分支（否则 `izmMode && !isHurt` 会把动画 timeScale 设成 0 = 卡住不转）。
	/// 三个字段实测都是 public 实例字段、非 readonly，直接反射赋值即可。
	///
	/// 关掉它 = 回到官方行为（旋转期间免疫）。
	/// </summary>
	internal const bool ImitaterBiteable = true;

	/// <summary>
	/// 把「免咬」相关字段按回可被咬的取值。目标组合 = `checkIZM=false, izmMode=true, isHurt=false`
	/// （三个都是实测 public 非 readonly 实例字段）：
	///   · `ProtectsFromBites` = `izmMode ? isHurt : true` ⇒ (true,false) 返回 **false** ⇒ 可被咬；
	///   · `ExplodeEntered` 只在 `!izmMode || isHurt` 时才 `EnableExplodeInvincibility()`
	///     ⇒ (true,false) ⇒ **不开无敌**；
	///   · `checkIZM` **必须是 false**，否则动画会僵住。`ExplodeProcessing`：
	///     ```
	///     if (checkIZM) { if (izmMode) { if (isHurt) ts = 1; else ts = 0;  ← 冻结！} }
	///     else          { if (IsIZMMode()) ts = 1; else ts = parent.timeScale × timeScale; }
	///     ```
	///     ⇒ `checkIZM=true` 配 `izmMode=true && !isHurt` 正好踩进 `ts = 0` 那一支（不转了）；
	///     设成 false 后它改看全局 `IsIZMMode()`，普通关卡为 false ⇒ 走正常 timeScale，旋转照旧。
	///   · `ResolveOwnerReferences` 里对 `izmMode` 的覆写是**单向置 true**
	///     （`if (checkIZM && IsInstanceValid && IsIZMMode()) izmMode = true;`），
	///     不会把我们写的 true 改回 false，所以 checkIZM=false 也不会被冲掉。
	///
	/// 为什么要逐帧写、而不是只在 `_Ready` 写一次：咬一下会触发 `IZMHurt`
	/// ⇒ `isHurt = true` + `EnableExplodeInvincibility()`（打开 `instance.invincible`）
	/// ⇒ 不压回去就变成「只咬得动一口」。
	/// </summary>
	private void TickBiteable()
	{
		try
		{
			object comp = FindField(this, "_explodeComponent");
			if (comp == null)
			{
				return;
			}
			// 字段 Info 全类型共用，找一个实例拿一次就够
			if (_riF_checkIZM == null)
			{
				_riF_checkIZM = FindFieldInfo(comp, "checkIZM");
				_riF_izmMode = FindFieldInfo(comp, "izmMode");
				_riF_isHurt = FindFieldInfo(comp, "isHurt");
				if (_riF_checkIZM == null || _riF_izmMode == null || _riF_isHurt == null)
				{
					_riMissing = true;
					ReportBiteOnce("「可被僵尸咬」未生效（组件字段名变了），已退回官方行为：旋转期间普通僵尸咬不动。");
					return;   // 字段名变了 ⇒ 不硬来，保持官方免咬行为
				}
			}
			if (_riMissing)
			{
				return;
			}
			_riF_checkIZM.SetValue(comp, false);  // ⇒ 旋转动画不被 timeScale=0 冻住
			_riF_izmMode.SetValue(comp, true);    // ⇒ ProtectsFromBites 返回 isHurt（false）
			_riF_isHurt.SetValue(comp, false);   // ⇒ 且 ExplodeEntered 不开无敌

			// 压掉被咬后引擎自己补上的无敌标记，否则一口之后就又咬不动了
			if (instance != null && GodotObject.IsInstanceValid(instance) && instance.invincible)
			{
				instance.invincible = false;
			}
			ReportBiteOnce("已打开「本体也可被普通僵尸啃咬」（关掉 ExplodeComponent 的旋转免咬闸门）。");
		}
		catch
		{
			// 不许抛：静默退回官方免咬行为。
		}
	}

	// ------------------------------------------------ 静态登记表（给入口每帧驱动用）

	/// <summary>场上存活的随机模仿者。`_Ready` 注册，`_ExitTree` 摘除。</summary>
	private static readonly System.Collections.Generic.List<RandomImitater> _live =
		new System.Collections.Generic.List<RandomImitater>();

	private static FieldInfo _riF_checkIZM;
	private static FieldInfo _riF_izmMode;
	private static FieldInfo _riF_isHurt;

	/// <summary>反射字段找不到 ⇒ 记住「这条路走不通」，不再逐帧白跑。</summary>
	private static bool _riMissing;

	/// <summary>入口的 `process_frame` 驱动：把场上每株模仿者调成可被咬。</summary>
	internal static void TickAllBiteable()
	{
		if (_riMissing || _live.Count == 0)
		{
			return;
		}
		for (int i = _live.Count - 1; i >= 0; i--)
		{
			RandomImitater p = _live[i];
			if (p == null || !GodotObject.IsInstanceValid(p))
			{
				_live.RemoveAt(i);   // 顺手回收失效引用
				continue;
			}
			p.TickBiteable();
		}
	}

	private static void Register(RandomImitater p)
	{
		if (!_live.Contains(p))
		{
			_live.Add(p);
		}
	}

	private void Unregister()
	{
		_live.Remove(this);
	}

	public override void _ExitTree()
	{
		base._ExitTree();
		try { Unregister(); } catch { }
	}

	// ------------------------------------------------ 彩色化（去掉产物的黑白滤镜）

	/// <summary>
	/// ★ 黑白是从哪来的（IL 证据）：
	///   游戏自己的 <c>TowerDefensePlantImitater.Explode()</c> 在生成植物/僵尸之后，
	///   会对新角色调用 <c>SetSpriteGroupShaderParameter("imitater", 1)</c>
	///   （植物那条走 <c>CallDeferred</c>，僵尸是直接调用）。
	///   而 <c>"imitater"</c> 是 <c>ShaderEffectComponent.ShaderEffectFlags</c> 里的
	///   **第 16 位**（`..cctor` 里 `"imitater" → 16`），<c>ApplyEffectFlags()</c>
	///   据此对每个精灵调 <c>AdobeAnimateSprite.SetRenderGrayscale(true)</c>
	///   ⇒ 产物整体变黑白。
	///
	///   本 Mod 的 <see cref="RiExplode"/> 走的正是 <c>base.Explode()</c>，
	///   所以同样继承了这层滤镜。后果是产物上所有依赖颜色区分的部件
	///   （防具、变体等）都失去辨识度 ⇒ 提供「彩色化」开关来关掉这层滤镜。
	///
	/// ★ 该位是**纯视觉**效果，不参与任何玩法判定 ⇒ 关掉它对平衡零影响。
	///
	/// ── 配色怎么落地：全局灰阶清扫（<see cref="SweepGrayOnField"/>）──
	/// 曾经的做法是「拍一张变身前的角色快照，事后取差集认出产物，再逐个纠正」。
	/// 实测它站不住：强制只出「流星」时，延帧窗放到 120 帧，角色注册表里仍然
	/// 始终没有它（它明明在场上放技能）⇒ 差集对它**永远**认不出来，于是它一直灰着。
	/// 反过来，玩家在同一帧里自己新种的模仿者**本体**却会被差集当成产物纠正掉。
	///
	/// 换成的依据是游戏本身的事实：全游戏只有模仿者这一条通道会打开灰阶
	/// （<c>SetRenderGrayscale(true)</c> 的调用点全部落在 <c>ShaderEffectComponent</c>
	/// 的 imitater 路径上）。所以场上带灰的角色只可能是两类：
	///   · 模仿者本体（<c>TowerDefensePlantImitater</c> 系 / <c>TowerDefenseZombieNormalImitater</c>）
	///     ⇒ 灰是它自己的样子，跳过不动；
	///   · 其余 ⇒ 必然是模仿者开出来的产物 ⇒ 「彩色化」开着就恢复原色。
	/// 这样注册时机、是否进注册表都不再重要，也不存在误伤本体的问题。
	/// </summary>

	private static FieldInfo _riF_effectFlags;   // ShaderEffectComponent._effectFlags
	private static int _riImitaterBit;           // "imitater" 对应的位（实测 16）
	private static bool _riColorUnavailable;     // 反射失败 ⇒ 彻底放弃，不再白跑

	/// <summary>
	/// 读取角色当前的 imitater（黑白）位。成功返回 true。
	/// 位号不写死，而是从游戏的 <c>ShaderEffectFlags</c> 字典里取 ⇒ 官方改位号也不会失效。
	/// </summary>
	private static bool TryReadImitaterFlag(TowerDefenseCharacter c, out bool on)
	{
		on = false;
		try
		{
			if (_riColorUnavailable || c == null)
			{
				return false;
			}
			if (_riF_effectFlags == null)
			{
				FieldInfo sef = typeof(TowerDefenseCharacter).GetField("shaderEffectComponent",
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (sef == null)
				{
					_riColorUnavailable = true;
					return false;
				}
				Type shType = sef.FieldType;
				FieldInfo dictF = shType.GetField("ShaderEffectFlags",
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
				FieldInfo efF = shType.GetField("_effectFlags",
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (dictF == null || efF == null)
				{
					_riColorUnavailable = true;
					return false;
				}
				Dictionary<string, int> dict = dictF.GetValue(null) as Dictionary<string, int>;
				int bit;
				if (dict == null || !dict.TryGetValue("imitater", out bit) || bit == 0)
				{
					_riColorUnavailable = true;
					return false;
				}
				_riImitaterBit = bit;
				_riF_effectFlags = efF;
			}

			object comp = FindField(c, "shaderEffectComponent");
			if (comp == null)
			{
				return false;
			}
			object raw = _riF_effectFlags.GetValue(comp);
			if (raw is int flags)
			{
				on = (flags & _riImitaterBit) != 0;
				return true;
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>反射：<c>ShaderEffectComponent.RefreshShaderNodes()</c>（公开方法）。</summary>
	private static MethodInfo _riM_refreshShaderNodes;

	/// <summary>
	/// 「这是本次变身开出来的产物吗」（延帧窗放宽到 120 帧后必须精确化，
	/// 否则会把玩家在这两秒里自己种的植物误当成产物）。
	///   · 落在变身那一格上 ⇒ 是（模仿者变身前会腾空自己那格，产物就种回这里）；
	///   · 带着模仿者的黑白位、或精灵挂着灰阶 ⇒ 是（灰是模仿者产物专属的表现）。
	/// </summary>
	internal static bool LooksLikeProduct(TowerDefenseCharacter c, Vector2I scanCell)
	{
		try
		{
			// 模仿者本体（含本 Mod 这张卡）：灰是它自己的样子，永远不算产物。
			// 实测玩家在同一帧里新种下另一株模仿者时，它会被差集窗当成产物纠正掉。
			if (c is TowerDefensePlantImitater || c is TowerDefenseZombieNormalImitater)
			{
				return false;
			}
			if (c is TowerDefenseGroundItemBase g && g.gridPos == scanCell)
			{
				return true;
			}
			bool on;
			if (TryReadImitaterFlag(c, out on) && on)
			{
				return true;
			}
			bool grayed;
			return TryReadSpriteGray(c, out grayed) && grayed;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// 灰阶读取：走场景树，成功返回 true（<paramref name="grayed"/> = 是否有精灵灰着）。
	/// 一个带灰阶接口的节点都找不到时返回 false ⇒ 调用方按「读不到」处理。
	/// </summary>
	private static bool TryReadSpriteGray(TowerDefenseCharacter c, out bool grayed)
	{
		grayed = false;
		try
		{
			int gray;
			int touched = WalkGrayNodes(c, null, out gray);
			if (touched <= 0)
			{
				return false;
			}
			grayed = gray > 0;
			return true;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>灰阶写入 / 读取的 Godot 方法名（精灵节点上都有）。</summary>
	private static readonly StringName _riGraySetMethod = "SetRenderGrayscale";
	private static readonly StringName _riGrayGetMethod = "IsRenderGrayscaleEnabled";

	/// <summary>遍历角色子树时的节点上限（防止极端场景树跑飞）。</summary>
	private const int GrayWalkCap = 4096;

	/// <summary>遍历用的暂存栈。</summary>
	private static readonly List<Node> _riWalkStack = new List<Node>();

	/// <summary>
	/// 在角色的**整棵场景树**里找带灰阶接口的节点。
	///   · <paramref name="write"/> 有值 ⇒ 逐个写入，返回命中的节点数；
	///   · <paramref name="write"/> 为 null ⇒ 只统计其中灰着的个数（写到 <paramref name="grayCount"/>）。
	///
	/// ★ 为什么不用 <c>ShaderEffectComponent._shaderNodes</c>：实测「精灵胆小菇 / 猫罐 /
	///   火坚果 / 苹果绿 …」这批复合植物的那张表**是空的**（日志「无精灵节点」），
	///   按它刷等于一个都没刷到 ⇒ 画面照旧灰。它们的精灵挂在延后拼装的子场景里，
	///   组件缓存建的时候还没挂上来。直接走场景树就不依赖游戏那份账。
	/// </summary>
	private static int WalkGrayNodes(TowerDefenseCharacter c, bool? write, out int grayCount)
	{
		grayCount = 0;
		Node root = c as Node;
		if (root == null)
		{
			return -1;
		}
		int touched = 0;
		int visited = 0;
		_riWalkStack.Clear();
		_riWalkStack.Add(root);
		while (_riWalkStack.Count > 0 && visited < GrayWalkCap)
		{
			int last = _riWalkStack.Count - 1;
			Node n = _riWalkStack[last];
			_riWalkStack.RemoveAt(last);
			visited++;
			if (n == null || !GodotObject.IsInstanceValid(n) || !n.HasMethod(_riGraySetMethod))
			{
				// 容器节点自己没有灰阶接口，但它的子节点有 ⇒ 继续往下走。
			}
			else
			{
				touched++;
				if (write.HasValue)
				{
					if (!write.Value)
					{
						// ★ 先 true 再 false：游戏里的 SetRenderGrayscale 开头就是
						//   「值没变 ⇒ 直接 return」，连 InvalidateRenderSnapshotCache
						//   和重绘都不做 ⇒ 渲染快照可能是**灰着时建的**（别处直接改过
						//   字段没走这个方法），光写 false 踢不掉它，画面就一直灰。
						//   造一次真变化把它作废；两次调用同帧完成，画面不会闪。
						n.Call(_riGraySetMethod, true);
					}
					n.Call(_riGraySetMethod, write.Value);
				}
				else if (n.Call(_riGrayGetMethod).AsBool())
				{
					grayCount++;
				}
			}
			foreach (Node ch in n.GetChildren())
			{
				_riWalkStack.Add(ch);
			}
		}
		return touched;
	}

	/// <summary>
	/// 把灰阶**直接写到角色的每个精灵节点上**（不走位掩码、也不靠按位重算）。
	///
	/// 前两条路都试过，都被游戏的实现挡死：
	///   · <c>SetSpriteGroupShaderParameter</c> → <c>SetEffectFlag</c> 在掩码没变化时直接 return；
	///   · <c>RefreshShaderNodes</c> → 它只遍历 <c>_shaderNodes</c> 那份缓存，
	///     而实测这批复合植物的那张表**是空的**（日志「无精灵节点」）。
	///
	/// ★ 写 false 时一律先 true 再 false：游戏里的 <c>SetRenderGrayscale</c> 开头就是
	///   「值没变 ⇒ 直接 return」，连 <c>InvalidateRenderSnapshotCache</c> 和重绘都不做
	///   ⇒ 万一渲染快照是灰着时建的（别处直接改过字段），光写 false 踢不掉它。
	///   两次调用在同一帧内完成，画面不会闪。
	/// </summary>
	private static void ForceSpriteGray(TowerDefenseCharacter c, bool grayscale)
	{
		try
		{
			int gray;
			WalkGrayNodes(c, grayscale, out gray);
		}
		catch
		{
			// 不许抛：纯视觉调整。
		}
	}

	/// <summary>
	/// 把角色的黑白位设成 <paramref name="grayscale"/>，并强制把精灵刷一遍。
	///
	/// 只调 <c>SetSpriteGroupShaderParameter</c> 是不够的：游戏里的
	/// <c>SetEffectFlag(mask, value, apply)</c> 在**掩码没有变化**时会直接返回，
	/// 连 <c>ApplyEffectFlags()</c> 都不执行 ⇒ 位本来就已经是关的（实测这几株「无黑白位」），
	/// 我们再把关写成关就是空操作，灰阶永远留在精灵上。
	/// <c>RefreshShaderNodes()</c> 则会遍历全部精灵节点，逐个
	/// <c>SetRenderGrayscale(IsImitaterEnabled())</c> + <c>QueueRedraw()</c> ⇒ 表现与位对齐。
	/// </summary>
	private static void ApplyColorize(TowerDefenseCharacter c, bool grayscale)
	{
		try
		{
			c.SetSpriteGroupShaderParameter("imitater", grayscale);
		}
		catch
		{
			// 不许抛：纯视觉调整，失败就保持原样。
		}
		try
		{
			object comp = FindField(c, "shaderEffectComponent");
			if (comp == null)
			{
				return;
			}
			if (_riM_refreshShaderNodes == null)
			{
				_riM_refreshShaderNodes = comp.GetType().GetMethod(
					"RefreshShaderNodes",
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			}
			if (_riM_refreshShaderNodes != null)
			{
				_riM_refreshShaderNodes.Invoke(comp, null);
			}
		}
		catch
		{
			// 同上：刷不动就算了，不影响对局。
		}
		// 最后一道：按位重算可能仍然落不回（游戏有直接把灰阶写到精灵上的路径），
		// 所以再按我们的意图硬写一遍，然后回读确认。
		ForceSpriteGray(c, grayscale);
	}

	/// <summary>
	/// ★ 全局灰阶清扫（不依赖「认出产物」）。
	///
	/// ── 为什么要有这一条 ──
	/// 之前所有实现都建立在「先认出哪几株是本次变身开出来的」之上：拍一张变身前的
	/// 角色快照，事后取差集。实测强制出「流星」时，延帧窗放宽到 120 帧（约 2 秒）、
	/// 植物早就在场上放技能了，注册表里仍然找不到它 ⇒ 差集这条路对它**永远**不成立，
	/// 于是它一直是灰的，而其它注册得上的植物早就被纠正好了。
	///
	/// ── 为什么可以不做身份识别 ──
	/// 全游戏只有模仿者这一条通道会打开灰阶（<c>SetRenderGrayscale(true)</c> 的调用点
	/// 全部落在 <c>ShaderEffectComponent</c> 的 imitater 路径上）。所以场上任何带灰的
	/// 角色，只可能是「模仿者本体」或「模仿者开出来的产物」两类：
	///   · 模仿者本体（<c>TowerDefensePlantImitater</c> 系、<c>TowerDefenseZombieNormalImitater</c>）
	///     ⇒ 灰是它自己的样子，跳过不动；
	///   · 其余 ⇒ 一定是产物 ⇒ 「彩色化」开着就该是原色，直接清。
	/// 这样注册时机、是否进注册表都不重要了。
	///
	/// ── 代价与收敛 ──
	/// 只在变身后的一段窗口内跑（<see cref="_sweepFrames"/>，约 10 秒），且每
	/// <see cref="SweepEveryFrames"/> 帧才走一次整棵树 ⇒ 全程最多一百来轮，开销可忽略。
	/// 刻意**不做**「场上干净就提前收工」：产物注册/变灰的时机没有保证，提前收窗
	/// 会让最晚出现的那一株一直灰着 —— 这正是这一整套改动的起因。
	/// </summary>
	private static int _sweepFrames;

	/// <summary>清扫窗口长度（帧）。约 10 秒，足够覆盖注册最慢的那类植物。</summary>
	private const int SweepWindowFrames = 600;

	/// <summary>某次模仿者变身时调用：打开清扫窗口。</summary>
	internal static void OpenGraySweep()
	{
		// 取「剩余」与「一整窗」的较大值：连续多次变身时窗口自动顺延，不会被截短。
		if (_sweepFrames < SweepWindowFrames)
		{
			_sweepFrames = SweepWindowFrames;
		}
	}

	/// <summary>
	/// 扫一遍场上所有角色，把「不是模仿者本体却带着灰阶」的那些恢复成原色。
	/// 返回本轮清掉的角色数（-1 表示没跑：开关关着 / 窗口已关）。
	/// </summary>
	internal static int SweepGrayOnField()
	{
		if (_sweepFrames <= 0 || !RandomImitaterEntry.ColorizeResult)
		{
			return -1;
		}
		_sweepFrames--;
		// ★ 节流：整棵场景树每 SweepEveryFrames 帧走一次即可（灰阶是持续状态，
		//   不是「只有一帧」的瞬时值），这样 10 秒窗口也只多跑一百来轮。
		if (++_sweepTick < SweepEveryFrames)
		{
			return 0;
		}
		_sweepTick = 0;

		SceneTree tree = TowerDefenseGroundItemBase.characterNode != null
			? TowerDefenseGroundItemBase.characterNode.GetTree() : null;
		Node scan = tree != null ? (Node)tree.Root : TowerDefenseGroundItemBase.characterNode;
		if (scan == null || !GodotObject.IsInstanceValid(scan))
		{
			return 0;
		}
		// ★ 从场景根往下扫，而不是只认角色注册表 / characterNode 的直接子节点：
		//   实测「流星」在注册表里根本取不到（延帧窗放到 120 帧仍一行命中都没有），
		//   但它确实在场上 ⇒ 它的挂载位置/注册时机与普通植物不一样，只能整棵树找。
		int fixedCount = 0;
		int chars = 0;
		List<Node> stack = _sweepStack;
		stack.Clear();
		stack.Add(scan);
		int visited = 0;
		while (stack.Count > 0 && visited < SweepNodeCap)
		{
			int last = stack.Count - 1;
			Node n = stack[last];
			stack.RemoveAt(last);
			visited++;
			if (n == null || !GodotObject.IsInstanceValid(n))
			{
				continue;
			}
			TowerDefenseCharacter c = n as TowerDefenseCharacter;
			if (c != null)
			{
				chars++;
				// 模仿者本体（含本 Mod 这张卡）：灰是它的正常外观，绝不能动。
				if (!(c is TowerDefensePlantImitater) && !(c is TowerDefenseZombieNormalImitater))
				{
					bool grayed;
					if (TryReadSpriteGray(c, out grayed) && grayed)
					{
						ApplyColorize(c, false);
						fixedCount++;
						// 记一笔：玩家中途关掉「彩色化」时，要把这些角色恢复成原生黑白。
						if (!_grayCleared.Contains(c))
						{
							_grayCleared.Add(c);
						}
					}
				}
			}
			foreach (Node ch in n.GetChildren())
			{
				stack.Add(ch);
			}
		}
		return fixedCount;
	}

	/// <summary>清扫节流：每几帧走一次整棵树。</summary>
	private const int SweepEveryFrames = 5;

	/// <summary>单轮最多访问的节点数（安全上限）。</summary>
	private const int SweepNodeCap = 20000;

	/// <summary>节流计数。</summary>
	private static int _sweepTick;

	/// <summary>清扫用的遍历栈（复用，避免每轮分配）。</summary>
	private static readonly List<Node> _sweepStack = new List<Node>();

	/// <summary>
	/// 被我们清过灰的产物。
	/// ★ 为什么必须记：玩家中途把「彩色化」关掉时，光停手不管是不够的 ——
	///   已经被我们恢复成原色的那些会一直是原色。关掉的那一刻按这份名单把灰
	///   补回去，开关才是真的可逆。
	/// </summary>
	private static readonly HashSet<TowerDefenseCharacter> _grayCleared =
		new HashSet<TowerDefenseCharacter>();

	/// <summary>把「被我们清过灰」的产物恢复成原生黑白（关掉彩色化开关时调用）。</summary>
	private static void RestoreNativeGray()
	{
		foreach (TowerDefenseCharacter c in _grayCleared)
		{
			if (c == null || !GodotObject.IsInstanceValid(c))
			{
				continue;
			}
			ApplyColorize(c, true);
		}
	}

	/// <summary>卸载/换局时清掉清扫状态。</summary>
	internal static void ClearGraySweep()
	{
		_sweepFrames = 0;
		_sweepTick = 0;
		_grayCleared.Clear();
	}

	/// <summary>
	/// 取「本次变身新出现的角色」，逐个做落地后的适配：
	///   · 属于 BOSS 三类的 ⇒ 交给 BGM 记账（切「BOSS战」）；
	///   · 墓碑系（吞噬者 / 钻卡版 / 爆破者 / 地雷 / 幽灵吞噬者）⇒ 关掉「必须有
	///     墓碑才动」这道闸门，幽灵吞噬者另加收尾替换（<see cref="NotifyGravebusterSpawned"/>）。
	/// </summary>
	private void ScanNewProducts(HashSet<TowerDefenseCharacter> before)
	{
		TowerDefenseManager mgr = TowerDefenseManager.Instance;
		if (mgr == null || !GodotObject.IsInstanceValid(mgr))
		{
			return;
		}
		TowerDefenseBattleCharacterRegistry reg = mgr.characterRegistry;
		if (reg == null)
		{
			return;
		}
		List<TowerDefenseCharacter> list = reg.GetActiveCharacters();
		if (list == null)
		{
			return;
		}
		for (int i = 0; i < list.Count; i++)
		{
			TowerDefenseCharacter c = list[i];
			if (c == null || before.Contains(c))
			{
				continue;
			}
			RandomImitaterEntry.AdaptProduct(c);
		}
	}

	/// <summary>变身前拍一张「场上已有角色」的快照，用于事后取差集。</summary>
	private static HashSet<TowerDefenseCharacter> SnapshotActiveCharacters()
	{
		var set = new HashSet<TowerDefenseCharacter>();
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return set;
			}
			TowerDefenseBattleCharacterRegistry reg = mgr.characterRegistry;
			if (reg == null)
			{
				return set;
			}
			List<TowerDefenseCharacter> list = reg.GetActiveCharacters();
			if (list == null)
			{
				return set;
			}
			for (int i = 0; i < list.Count; i++)
			{
				TowerDefenseCharacter c = list[i];
				if (c != null)
				{
					set.Add(c);
				}
			}
		}
		catch
		{
		}
		return set;
	}

	/// <summary>
	/// 入口的 `process_frame` 驱动：把「彩色化」开关应用到场上所有该恢复原色的角色。
	///
	/// ★ 开关关掉时（要原生黑白）不用我们做什么：游戏自己会重新把灰阶打开。
	/// ★ 开关打开时靠 <see cref="SweepGrayOnField"/> 的整树清扫兜住，它只在变身后
	///   的一段窗口内跑；这里额外保证「窗口已经关掉之后，玩家才把开关打开」这种情况
	///   也能立刻生效 —— 那种时刻重新开一个窗口即可。
	/// </summary>
	internal static void TickAllColorize()
	{
		if (_riColorUnavailable)
		{
			return;
		}
		try
		{
			bool on = RandomImitaterEntry.ColorizeResult;
			if (on && !_colorizeWasOn)
			{
				// 关 → 开：立刻扫一遍，把场上残留的灰恢复掉。
				OpenGraySweep();
			}
			else if (!on && _colorizeWasOn)
			{
				// 开 → 关：把我们清过灰的产物恢复成原生黑白（见 _grayCleared 的说明）。
				RestoreNativeGray();
			}
			_colorizeWasOn = on;

			if (!_colorizeReported && _sweepFrames > 0)
			{
				_colorizeReported = true;
				RandomImitaterEntry.LogStatic("彩色化已生效：本 Mod 抽出的植物/僵尸不再套用模仿者的黑白滤镜"
					+ "（可在游戏内「Mod 管理 → 配置」里关闭，恢复原版黑白）。");
			}
		}
		catch
		{
			// 不许抛：逐帧回调里抛出会打断整帧驱动。
		}
	}

	/// <summary>上一帧「彩色化」开关的状态（用于识别 关→开 的那一刻）。</summary>
	private static bool _colorizeWasOn = true;

	/// <summary>「彩色化已生效」这条日志只打一次。</summary>
	private static bool _colorizeReported;

	/// <summary>卸载时清掉追踪表，避免把上一局的引用带到下一局。</summary>
	internal static void ClearColorizeTracking()
	{
		try
		{
			ClearGraySweep();
			_colorizeReported = false;
			_colorizeWasOn = true;
		}
		catch
		{
		}
		try
		{
			// 变身产物延帧窗与墓碑系的追踪表同样是静态的 ⇒ 一起清。
			RandomImitaterEntry.ClearGravebusterTracking();
		}
		catch
		{
		}
	}

	/// <summary>「接管变身」的成功/失败日志只打一次（静态：全场所有实例共用，避免每株一行）。</summary>
	private static bool _riLogged;

	/// <summary>
	/// 组件就绪后把 `ExplodeComponent.OnExplode` 换成 <see cref="RiExplode"/>。
	/// 不抛，失败就退回官方模仿者的行为（随机照样出，只是落位/被咬限制不生效）。
	/// </summary>
	public override void _Ready()
	{
		base._Ready();     // 基类在此取 ExplodeComponent 并绑定它自己的 Explode
		if (_riHooked)
		{
			return;
		}
		string why = TryHookExplode();
		if (why == null)
		{
			_riHooked = true;
			if (ImitaterBiteable)
			{
				Register(this);   // 由入口的 process_frame 逐帧保持可被咬
			}
			ReportOnce("已接管变身（先腾空自己再开出），落位规则按普通植物走。");
			return;
		}
		// 拿不到组件/字段 ⇒ 不重复刷日志，但第一次要说出来，否则只能靠"全是僵尸"猜。
		ReportOnce("接管变身未生效（已退回原生变身 ⇒ 不许叠种/容器/墓碑弹坑这几条落位限制不起作用），"
			+ "原因：" + why);
	}

	/// <summary>成功返回 null，失败返回原因。</summary>
	private string TryHookExplode()
	{
		try
		{
			object comp = FindField(this, "_explodeComponent");
			if (comp == null)
			{
				return "找不到字段 _explodeComponent";
			}
			// ⚠️ `ExplodeComponent` 继承 `CharacterComponentRuntime`，是**普通 C# 类**，
			//    不是 GodotObject ⇒ 不能拿 GodotObject.IsInstanceValid 去校验它
			//    （之前写 `(GodotObject)comp` 直接 InvalidCastException，整个接管失败 ⇒
			//     退回原生变身 + 已收窄的格类型 ⇒ 植物被校验挡掉、僵尸也照样不吃）。
			//    组件生命周期用 `CharacterComponentRuntime.IsReleased` 判断，拿不到就只用 null 检查。
			if (IsReleasedComponent(comp))
			{
				return "_explodeComponent 已释放";
			}
			FieldInfo evt = FindFieldInfo(comp, "OnExplode");
			if (evt == null)
			{
				return "ExplodeComponent 里没有 OnExplode 字段";
			}
			MethodInfo mi = typeof(RandomImitater).GetMethod(nameof(RiExplode),
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (mi == null)
			{
				return "找不到 RiExplode 方法（编译产物不对）";
			}
			// 委托类型取自字段自身类型（ExplodeComponent+ExplodeEventHandler），
			// 编译期不需要知道这个嵌套类型；它的 Invoke() 无参数，与 RiExplode() 一致。
			Delegate d = Delegate.CreateDelegate(evt.FieldType, this, mi);
			evt.SetValue(comp, d);   // 整体替换 ⇒ 基类那个处理器同时被摘掉
			return null;
		}
		catch (Exception ex)
		{
			// `_Ready` 里绝不能抛（抛出会连带整个角色创建失败 ⇒ 植物种不出来）。
			return "异常：" + ex.Message;
		}
	}

	/// <summary>
	/// 组件是否已被释放。用反射读 `CharacterComponentRuntime.IsReleased`
	/// （官方 `ImitaterW._ExitTree` 判的就是这个）；读不到就当作「没释放」，
	/// 宁可不拦 —— 拦错了会让整个接管失效，那比少一次防御性检查严重得多。
	/// </summary>
	private static bool IsReleasedComponent(object comp)
	{
		try
		{
			for (Type t = comp.GetType(); t != null; t = t.BaseType)
			{
				PropertyInfo p = t.GetProperty("IsReleased",
					BindingFlags.Public | BindingFlags.NonPublic |
					BindingFlags.Instance | BindingFlags.DeclaredOnly);
				if (p != null && p.PropertyType == typeof(bool))
				{
					return (bool)p.GetValue(comp);
				}
			}
		}
		catch { }
		return false;
	}

	private static void ReportOnce(string msg)
	{
		if (_riLogged)
		{
			return;
		}
		_riLogged = true;
		RandomImitaterEntry.LogStatic(msg);
	}

	/// <summary>「可被咬」是否真的生效过（字段找得到）——只报一次。</summary>
	private static bool _riBiteReported;

	private static void ReportBiteOnce(string msg)
	{
		if (_riBiteReported)
		{
			return;
		}
		_riBiteReported = true;
		RandomImitaterEntry.LogStatic(msg);
	}

	/// <summary>
	/// 自定义变身：先把自己从格子上腾走，再走**基类原本的抽卡 + 校验 + 落地**逻辑。
	/// 顺序照抄官方第 7 章的 `TowerDefensePlantImitaterW.Explode()`。
	/// ★ 必须保持**无参**且**只有一个同名方法**：接管是靠
	///   `GetMethod(nameof(RiExplode))` 反射取它再绑成 `OnExplode` 委托，
	///   一旦重载出第二个 RiExplode，GetMethod 会抛 AmbiguousMatchException ⇒ 接管整体失效。
	/// </summary>
	private void RiExplode()
	{
		// 彩色化 / BOSS 识别共用：变身前先拍下场上已有角色，事后取差集就知道本次开出了什么。
		// 放在 try 外也要能拿到，所以先算好。
		HashSet<TowerDefenseCharacter> before = null;
		try
		{
			before = SnapshotActiveCharacters();
		}
		catch
		{
			before = null;
		}

		// 变身前记下自己那格：抽出的植物会种回这一格，延帧差集用它做精确过滤
		// （见 RandomImitaterEntry.BeginGravebusterScan）。取不到不影响其它功能。
		Vector2I myCell = default(Vector2I);
		try
		{
			myCell = this.gridPos;
		}
		catch
		{
		}

		try
		{
			// ① 腾位置：同步清掉 slot / characterSlotDictionary / characterList
			//    （freeInstance = false ⇒ 不立即 QueueFree，节点还要用于收尾；与官方一致）
			Destroy(false);

			// ①.5/③ 放宽只在 base.Explode() 这一句里有效，成对放在同一个 try/finally
			//        内，保证任何异常路径都会还原（不会留下「永久放宽」的脏配置）。
			//      不做这一步的话，原生重抽循环会把本格种不下的植物**永久剔除**后再抽
			//      （`TowerDefensePlantImitater.Explode()` IL_00EC `Array.Remove`），
			//      僵尸那一支不校验 ⇒ 墓碑格/弹坑格上出僵尸的概率会从 35% 涨到接近 70%。
			try
			{
				WidenPoolPlants();

				// ② 抽卡 + 落点校验 + 种下/生成，全部交给基类原逻辑：
				//    它读的就是本 Mod 运行时注册的混合卡池 packetBank，行为与今天完全一致，
				//    只是现在校验看到的是**空格子**，植物不会再被自己挡掉。
				base.Explode();
			}
			finally
			{
				RestorePoolPlants();
			}
		}
		catch
		{
			// 不许抛：变身回调里抛出会让战斗流程崩。
		}

		try
		{
			// ②.4 落地适配：抽出 BOSS ⇒ 切「BOSS战」BGM；抽出墓碑系植物 ⇒ 放开
			//      「必须有墓碑才动」的闸门。
			//      ★ 当帧差集只认得出**僵尸**（Create + AddChild 是同步注册的）；
			//        植物走 Plant()，里面有 CallDeferred ⇒ 下一帧才在册，所以另外
			//        开一个延帧窗（BeginGravebusterScan）按「变身那一格上新出现的
			//        植物」补认。1.10.1 只有当帧这一句 ⇒ 墓碑系植物从来没被处理过。
			if (before != null)
			{
				ScanNewProducts(before);
				RandomImitaterEntry.BeginGravebusterScan(before, myCell);
			}

			// ★ 打开全局灰阶清扫窗口：不依赖「认出产物」，直接把场上除模仿者本体外
			//   所有带灰的角色恢复原色（「流星」根本不在角色注册表里，差集那条路永远抓不到它）。
			RandomImitater.OpenGraySweep();
		}
		catch
		{
		}

		try
		{
			// ③ 收尾：注销 + 出组 + 释放（官方 ImitaterW 尾段 IL_03C3-IL_03E4 同款）
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr != null && GodotObject.IsInstanceValid(mgr))
			{
				mgr.CharacterUnregister(this);
			}
			RemoveFromGroup("Character");
			if (GodotObject.IsInstanceValid(this))
			{
				QueueFree();
			}
		}
		catch
		{
			// 静默
		}
	}

	/// <summary>
	/// ★ 「地形临时放宽」的实现（对应 <c>RiExplode</c> 里的 ①.5/③）。
	///
	/// ── 要解决的问题 ──
	/// 原生 <c>TowerDefensePlantImitater.Explode()</c> 的抽卡循环是：
	///   抽到植物 → <c>cell.CanPacketPlant(那张植物自己的配置)</c> 判 false
	///            → <c>Array.Remove(该条目)</c> 后**重新抽**（永久剔除）；
	///   抽到僵尸 → 不校验，直接生成。
	/// 于是「本格种得下的植物」越少，植物条目被剔得越干净，结果就越偏僵尸。
	/// 实测（PCK 138 个植物配置）：草地格还能种 74% 的植物 ⇒ 出僵尸约 42%；
	/// 纯水格约 51% ⇒ 约 51%；墓碑格/弹坑格只剩 24% ⇒ 约 69%。
	/// 玩家看到的会是「说明写 65:35，墓碑上却几乎必出僵尸」。
	///
	/// ── 做法 ──
	/// 变身前后各做一次：把卡池里**每张植物**的 <c>plantGridType</c> 临时并上
	/// 「随机模仿者自己的地形列表」（同一份地形口径 ⇒ 模仿者能种的格子，
	/// 卡池里的植物就一定能种，重抽循环一次都不会命中剔除分支）。
	/// 之所以是这两份列表而不是本格的地形层：IL_0404 要求格上是墓碑时列表必须含
	/// GRAVESTONE(8)、是弹坑时必须含 CRATER(9)，直接借本格地形层反而不含这两项。
	///
	/// ── 为什么这样不会污染游戏其它部分 ──
	///   · 抽卡走 <c>TowerDefenseManager.GetPacketConfig(key)</c>，IL_0175 起
	///     <c>Resource.Duplicate(true)</c> ⇒ 校验/落地用的是**深拷贝**；
	///     还原共享配置之后，本次落地的判定依据仍留在拷贝里，两不相干。
	///   · 放宽窗口只包住 <c>base.Explode()</c> 这一个同步调用，中间不插自己的
	///     逻辑 ⇒ 不存在「玩家恰好在这半句话里种花」的时机（游戏是单线程帧循环）。
	///   · 铲掉/死亡时槽位靠 <c>CharacterDestroy</c> IL_013E、<c>RemoveCharacter</c>
	///     IL_013E 末尾**按对象身份**遍历 slot 键置空来释放，不看植物的地形列表 ⇒
	///     放宽种下的植物不会漏槽，也不会让后续植物叠上来。
	///   · 存档侧 <c>_LoadCellInto</c> 是把 <c>slot</c> 按存进去的键值原样写回
	///     （IL_02B1-IL_0370），不重新按地形推导 ⇒ 不会因为放宽而读档丢植物。
	///
	/// ★ 反射失败/配置找不到时一律**静默放弃**：最坏退化成「和原生模仿者一样
	///   在某些地形更容易出僵尸」，不会报错、不会崩、也不会把卡池写坏。
	/// </summary>
	private void WidenPoolPlants()
	{
		try
		{
			_widenDepth++;
			if (_widenDepth > 1)
			{
				// 同一帧里第二株模仿者也在变身：外层已经放宽过了，这里只加计数，
				// 避免内层提前把表还原、外层还以为处于放宽状态。
				return;
			}
			Godot.Collections.Array<TowerDefenseEnum.PLANTGRIDTYPE> want = RiTerrainList();
			foreach (string key in RandomImitaterEntry.PoolPlantKeys)
			{
				if (string.IsNullOrEmpty(key))
				{
					continue;
				}
				try
				{
					// 必须是 ReadOnly 那份：GetPacketConfig 返回深拷贝，改它等于没改。
					TowerDefensePacketConfig pc
						= TowerDefenseManager.GetPacketConfigReadOnly(key);
					if (pc == null || !GodotObject.IsInstanceValid(pc))
					{
						continue;
					}
					if (!(pc.characterConfig is TowerDefensePlantConfig plant))
					{
						continue;   // 卡池名字对不上植物配置 ⇒ 不动
					}
					if (!GodotObject.IsInstanceValid(plant))
					{
						continue;
					}
					// ①「必须种在某张卡上」这道覆盖闸门先放开（紫卡/机枪射手）：
					//    它和地形无关，下面地形那几条 continue 不能把它一起跳过。
					WidenCover(key, pc, plant);

					// ② 地形放宽：want 取不到时只跳过这一步，覆盖放宽照做。
					if (want == null)
					{
						continue;
					}
					Godot.Collections.Array<TowerDefenseEnum.PLANTGRIDTYPE> cur
						= plant.plantGridType;
					if (cur == null)
					{
						continue;
					}
					// ★ 防御：万一卡池里混进了本卡自己（或任何与 want 同一实例的配置），
					//   `cur` 和 `want` 就是同一个数组 ⇒ 边遍历 want 边 Add 会死循环。
					if (ReferenceEquals(cur, want)
						|| string.Equals(key, RandomImitaterEntry.MyKey, StringComparison.Ordinal))
					{
						continue;
					}
					// 原本含 ALL(-1) 的卡本来就哪都能种，跳过 ⇒ 少写一份，
					// 也避免 restore 之后行为漂移（它原来就不需要放宽）。
					if (cur.Contains(RiTerrainAll))
					{
						continue;
					}
					List<TowerDefenseEnum.PLANTGRIDTYPE> saved
						= new List<TowerDefenseEnum.PLANTGRIDTYPE>(cur.Count);
					foreach (TowerDefenseEnum.PLANTGRIDTYPE g in cur)
					{
						saved.Add(g);
					}
					bool added = false;
					foreach (TowerDefenseEnum.PLANTGRIDTYPE g in want)
					{
						if (!cur.Contains(g))
						{
							cur.Add(g);
							added = true;
						}
					}
					if (added)
					{
						_widened[key] = saved;   // 记下原样，restore 用整表覆盖回去
					}
				}
				catch
				{
					// 单张卡失败不影响其它卡
				}
			}
			if (want == null && !_coverReported)
			{
				_coverReported = true;
				RandomImitaterEntry.LogStatic("读不到本卡的地形列表 ⇒ 只放开「必须种在某张卡上」这道限制，"
					+ "地形仍按原生口径。");
			}
		}
		catch
		{
			// 不许抛
		}
	}

	/// <summary>
	/// ★ 临时放开「这张卡必须种在某张卡上」这道闸门（紫卡，例：机枪射手=双发射手的覆盖卡）。
	///
	/// ── 闸门在哪 ──
	/// <c>TowerDefenseCellInstance.CanPacketPlant(cfg, arg2, arg3, arg4)</c>，
	/// 模仿者抽卡校验调的是 <c>CanPacketPlant(cfg, false, false, true)</c>
	/// （<c>TowerDefensePlantImitaterW.Explode()</c> IL_0121–IL_012C）⇒ arg4=true，闸门生效：
	///   IL_050e arg4 为真 → IL_0516 <c>GetPlantCover()</c> 非空 → IL_0526 起在本格里找一个
	///   「合法宿主」（非盾牌、阵营一致、非全息、<c>plantGridOverrideType</c> 相同，且名字
	///   出现在 cover 列表里）→ 找到 ⇒ IL_05d1 <c>return true</c>；
	///   **找不到 ⇒ IL_05f6 <c>GetCoverCanDirectPlant()</c> 为 false 就 <c>return false</c>**
	///   （IL_05fd–IL_05fe，这是本方法里唯一一处提前 <c>ret false</c>）。
	/// <c>GetCoverCanDirectPlant()</c> 只有在卡带 <c>_override</c> 时才可能为真，且杂交版的
	/// 紫卡没开这个开关 ⇒ 这些卡在抽卡校验里**永远**判 false ⇒ 命中重抽循环的
	/// <c>Array.Remove</c>（IL_00EB）被永久剔除 ⇒ 玩家从没见过紫卡被抽出来。
	/// 落地那一次也不放过：<c>TowerDefensePacketConfig.Plant(...)</c> 内部 IL_0196–IL_01AA
	/// 再查一次 <c>CanPacketPlant</c> ⇒ 单清「抽卡校验」不够，得让 cover 本身为空。
	///
	/// ── 做法 ──
	/// 让 IL_0516 那次 <c>get_Count</c> 变成 0，整段闸门直接跳过（<c>ble IL_05ff</c>）：
	///   · <c>plant.characterConfig.plantCover</c> 清空 —— 闸门读的是 <c>GetPlantCover()</c>，
	///     它优先取 <c>_override.plantCover</c>（非空时），否则退回 <c>characterConfig.plantCover</c>
	///     ⇒ 两处都要清，只清 _override 会被退回值盖住；
	///   · <c>plant.extendCoverDictionary</c> 清空 —— 另两处 IL_00F2（开头，arg3=false 时
	///     必查扩展格）与 IL_061D（cover 之后）都以它非空为条件 <c>return false</c>。
	/// ★ 不动 <c>GetCoverCanDirectPlant()</c>/<c>coverCanDirectPlant</c>：cover 一空，
	///   那段根本不会执行 ⇒ 不必去碰一个语义是「能不能直接种」的公开开关。
	/// ★ 不动 <c>plantCoverSelf</c>/<c>plantCoverAll</c>：它们只出现在**放行**分支
	///   （IL_06BD 起，同种植物已在本格时提前 return true），留着不碍事。
	/// ★ 连 <c>extendCoverDictionary</c> 一起清 ⇒ 跨格（多格）的覆盖卡也能进池。
	///   代价是这类卡被抽出来时只会落在**主格**上：模仿者调的是
	///   <c>Plant(gridPos, true, false, default, false, false)</c>，第 5 个参数（扩展格开关）
	///   本来就是 false ⇒ 原生从来没给过它扩展格，我们没有让情况变坏，
	///   只是那株不会占第二格。落地副本的这几份表会保持为空（副本在清空期间生成），
	///   读它们的分支都以 <c>Count &gt; 0</c> 为前提 ⇒ 不会空引用。
	/// ★ 只在 <c>base.Explode()</c> 这个同步窗口内为空，出窗口由
	///   <see cref="RestorePoolPlants"/> 原样写回 ⇒ 玩家自己种紫卡仍必须先种宿主植物。
	///   窗口内落地用的配置是 <c>GetPacketConfig</c> 的深拷贝，还原共享配置
	///   不会回头改已经种下去的那株。
	/// </summary>
	private static void WidenCover(string key, TowerDefensePacketConfig pc,
		TowerDefensePlantConfig plant)
	{
		Godot.Collections.Array<string> cover = plant.plantCover;
		TowerDefensePacketOverride ov = pc._override;
		if (ov != null && !GodotObject.IsInstanceValid(ov))
		{
			ov = null;
		}
		Godot.Collections.Array<string> ovCover = (ov == null) ? null : ov.plantCover;
		Godot.Collections.Dictionary<Vector2I, string> ext = plant.extendCoverDictionary;

		bool hasCover = cover != null && cover.Count > 0;
		bool hasOvCover = ovCover != null && ovCover.Count > 0;
		bool hasExt = ext != null && ext.Count > 0;
		if (!hasCover && !hasOvCover && !hasExt)
		{
			return;   // 这张卡没有覆盖限制 ⇒ 一个字都不改
		}

		if (_widenedCover.ContainsKey(key))
		{
			return;   // 本轮已经处理过（卡池键唯一，正常进不来）
		}
		CoverSnapshot snap = new CoverSnapshot();
		if (hasCover)
		{
			foreach (string s in cover)
			{
				snap.PlantCover.Add(s);
			}
		}
		if (hasOvCover)
		{
			snap.HasOverride = true;
			foreach (string s in ovCover)
			{
				snap.OverrideCover.Add(s);
			}
		}
		if (hasExt)
		{
			foreach (KeyValuePair<Vector2I, string> kv in ext)
			{
				snap.Extend.Add(new KeyValuePair<Vector2I, string>(kv.Key, kv.Value));
			}
		}
		_widenedCover[key] = snap;

		if (hasCover)
		{
			cover.Clear();
		}
		if (hasOvCover)
		{
			ovCover.Clear();
		}
		if (hasExt)
		{
			ext.Clear();
		}
		if (hasCover || hasOvCover || hasExt)
		{
			_coverCount++;
		}
	}

	/// <summary>一张卡被清空前的三份覆盖数据。</summary>
	private sealed class CoverSnapshot
	{
		/// <summary><c>characterConfig.plantCover</c> 原值。</summary>
		public readonly List<string> PlantCover = new List<string>();

		/// <summary><c>_override.plantCover</c> 原值（<see cref="HasOverride"/> 为真时才有意义）。</summary>
		public readonly List<string> OverrideCover = new List<string>();

		/// <summary>这张卡原本有没有 <c>_override</c> 对象（没有就绝不能凭空造）。</summary>
		public bool HasOverride;

		/// <summary><c>extendCoverDictionary</c> 原值。</summary>
		public readonly List<KeyValuePair<Vector2I, string>> Extend =
			new List<KeyValuePair<Vector2I, string>>();
	}

	/// <summary>放宽过的植物 key → 它的覆盖闸门原值（还原用）。</summary>
	private static readonly Dictionary<string, CoverSnapshot> _widenedCover =
		new Dictionary<string, CoverSnapshot>(StringComparer.Ordinal);

	/// <summary>本轮被放开覆盖限制的卡数（只用于报一次日志）。</summary>
	private static int _coverCount;

	/// <summary>「读不到地形列表」那条降级提示只报一次。</summary>
	private static bool _coverReported;

	/// <summary>把 <see cref="WidenPoolPlants"/> 改过的配置整表还原。</summary>
	private void RestorePoolPlants()
	{
		if (_widenDepth > 0)
		{
			_widenDepth--;
		}
		if (_widenDepth > 0)
		{
			return;   // 还有别株模仿者在这个放宽窗口里，等最外层那一次再还原
		}
		int n = _coverCount;
		_coverCount = 0;
		if (n > 0)
		{
			RandomImitaterEntry.LogStatic("本次变身临时放开「紫卡必须先种在宿主植物上」的限制 ⇒ 已抽出覆盖型"
				+ "（组合/进阶）植物 " + n + " 张可入池；变身结束后限制原样恢复，"
				+ "玩家自己种这些卡仍须先种底座。");
		}
		RestoreCover();
		if (_widened.Count == 0)
		{
			return;
		}
		foreach (KeyValuePair<string, List<TowerDefenseEnum.PLANTGRIDTYPE>> kv in _widened)
		{
			try
			{
				TowerDefensePacketConfig pc
					= TowerDefenseManager.GetPacketConfigReadOnly(kv.Key);
				if (pc == null || !GodotObject.IsInstanceValid(pc))
				{
					continue;
				}
				if (!(pc.characterConfig is TowerDefensePlantConfig plant)
					|| !GodotObject.IsInstanceValid(plant))
				{
					continue;
				}
				Godot.Collections.Array<TowerDefenseEnum.PLANTGRIDTYPE> cur
					= plant.plantGridType;
				if (cur == null)
				{
					continue;
				}
				cur.Clear();
				foreach (TowerDefenseEnum.PLANTGRIDTYPE g in kv.Value)
				{
					cur.Add(g);
				}
			}
			catch
			{
				// 单张还原失败不阻断其余
			}
		}
		_widened.Clear();
	}

	/// <summary>
	/// 还原 <see cref="WidenCover"/> 清掉的三份覆盖数据（整表覆盖回去）。
	/// ★ 只还原「我们清过」的卡；对象失效/取不到配置时跳过该卡（不阻断其余）。
	/// </summary>
	private static void RestoreCover()
	{
		if (_widenedCover.Count == 0)
		{
			return;
		}
		foreach (KeyValuePair<string, CoverSnapshot> kv in _widenedCover)
		{
			try
			{
				TowerDefensePacketConfig pc
					= TowerDefenseManager.GetPacketConfigReadOnly(kv.Key);
				if (pc == null || !GodotObject.IsInstanceValid(pc))
				{
					continue;
				}
				if (!(pc.characterConfig is TowerDefensePlantConfig plant)
					|| !GodotObject.IsInstanceValid(plant))
				{
					continue;
				}
				CoverSnapshot snap = kv.Value;
				if (snap.PlantCover.Count > 0)
				{
					Godot.Collections.Array<string> cover = plant.plantCover;
					if (cover != null)
					{
						cover.Clear();
						foreach (string s in snap.PlantCover)
						{
							cover.Add(s);
						}
					}
				}
				if (snap.HasOverride)
				{
					TowerDefensePacketOverride ov = pc._override;
					if (ov != null && GodotObject.IsInstanceValid(ov))
					{
						Godot.Collections.Array<string> ovCover = ov.plantCover;
						if (ovCover != null && snap.OverrideCover.Count > 0)
						{
							ovCover.Clear();
							foreach (string s in snap.OverrideCover)
							{
								ovCover.Add(s);
							}
						}
					}
				}
				if (snap.Extend.Count > 0)
				{
					Godot.Collections.Dictionary<Vector2I, string> ext
						= plant.extendCoverDictionary;
					if (ext != null)
					{
						ext.Clear();
						foreach (KeyValuePair<Vector2I, string> e in snap.Extend)
						{
							ext[e.Key] = e.Value;
						}
					}
				}
			}
			catch
			{
				// 单张还原失败不阻断其余
			}
		}
		_widenedCover.Clear();
	}

	/// <summary>
	/// 统计卡池里带「必须种在某张卡上」限制的植物卡数（紫卡），用于开局那条日志。
	/// ★ 只读不改 ⇒ 任何时候调用都安全；取不到配置按 0 计。
	/// </summary>
	internal static int CountCoverPoolPlants()
	{
		int n = 0;
		try
		{
			foreach (string key in RandomImitaterEntry.PoolPlantKeys)
			{
				if (string.IsNullOrEmpty(key))
				{
					continue;
				}
				try
				{
					TowerDefensePacketConfig pc
						= TowerDefenseManager.GetPacketConfigReadOnly(key);
					if (pc == null || !GodotObject.IsInstanceValid(pc))
					{
						continue;
					}
					if (!(pc.characterConfig is TowerDefensePlantConfig plant)
						|| !GodotObject.IsInstanceValid(plant))
					{
						continue;
					}
					if ((plant.plantCover != null && plant.plantCover.Count > 0)
						|| (plant.extendCoverDictionary != null
							&& plant.extendCoverDictionary.Count > 0))
					{
						n++;
					}
				}
				catch
				{
					// 单张失败不影响统计
				}
			}
		}
		catch
		{
			// 静默
		}
		return n;
	}

	/// <summary>
	/// 随机模仿者自己那份地形列表（= 打包进 .tres 的 <c>plantGridType</c>）。
	/// 拿它当「放宽口径」的唯一来源，避免这里再抄一遍常量跟 .tres 走偏。
	/// </summary>
	private static Godot.Collections.Array<TowerDefenseEnum.PLANTGRIDTYPE> RiTerrainList()
	{
		try
		{
			TowerDefensePacketConfig mine
				= TowerDefenseManager.GetPacketConfigReadOnly(RandomImitaterEntry.MyKey);
			if (mine == null || !GodotObject.IsInstanceValid(mine))
			{
				return null;
			}
			if (!(mine.characterConfig is TowerDefensePlantConfig pc))
			{
				return null;
			}
			return pc.plantGridType;
		}
		catch
		{
			return null;
		}
	}

	/// <summary><c>PLANTGRIDTYPE.ALL</c>（= -1）：本来就全地形可种的卡不需要放宽。</summary>
	private const TowerDefenseEnum.PLANTGRIDTYPE RiTerrainAll
		= (TowerDefenseEnum.PLANTGRIDTYPE)(-1);

	/// <summary>放宽过的植物 key → 它原本的 plantGridType 整表（还原用）。</summary>
	private static readonly Dictionary<string, List<TowerDefenseEnum.PLANTGRIDTYPE>>
		_widened = new Dictionary<string, List<TowerDefenseEnum.PLANTGRIDTYPE>>(
			StringComparer.Ordinal);

	/// <summary>放宽的嵌套计数：只有回到 0 才真正还原（见 <c>WidenPoolPlants</c>）。</summary>
	private static int _widenDepth;

	/// <summary>逐级遍历基类找私有字段值（C# 默认不查基类自己声明的 private 字段）。</summary>
	private static object FindField(object target, string name)
	{
		FieldInfo f = FindFieldInfo(target, name);
		return (f == null) ? null : f.GetValue(target);
	}

	private static FieldInfo FindFieldInfo(object target, string name)
	{
		if (target == null)
		{
			return null;
		}
		for (Type t = target.GetType(); t != null; t = t.BaseType)
		{
			FieldInfo f = t.GetField(name,
				BindingFlags.Public | BindingFlags.NonPublic |
				BindingFlags.Instance | BindingFlags.DeclaredOnly);
			if (f != null)
			{
				return f;
			}
		}
		return null;
	}
}
