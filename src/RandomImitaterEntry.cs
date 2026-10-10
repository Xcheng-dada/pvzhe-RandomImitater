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
///     · 65% 开出随机植物；
///     · 35% 开出随机僵尸（敌对，会朝玩家的房子走）。
///   植物与僵尸**各自等概率**：每张植物、每只僵尸中奖率相同，与卡池分类无关。
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
	private const string MyKey = "RandomImitater";

	/// <summary>运行时注册的混合卡池名 —— 新植物场景里的 `packetBank` 指向它。</summary>
	private const string CustomBank = "RandomImitaterMixed";

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
	/// 想改：0.7 = 七成植物，0.3 = 三成植物，1.0 = 只出植物，0.0 = 只出僵尸。
	/// 改完 `BuildWeighted` 会自动重新搜索最优的重复次数，无需手工调列表。
	///
	/// ✅ 现在 `plantGridType` 收窄成 [2,6,5]（和普通植物一致）也照样生效：
	/// 自定义的 `RandomImitater.RiExplode()` 会**先把自己从格子上腾走**
	/// （`Destroy(false)` 同步清空 slot），再交给基类抽卡/落点校验，
	/// 所以校验看到的一格是空地，植物不会被自己挡掉。
	/// （旧版必须留 [-1] 的原因就是没有这一步 —— 见 RandomImitater 类注释。）
	/// </summary>
	private const double PlantChance = 0.65;

	/// <summary>僵尸在卡池里的分类名。</summary>
	private const string ZombieCat = "Zombie";

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
	/// ★ 是否允许抽出「BOSS 僵尸」。
	///
	/// 卡池里确实有 3 个 BOSS：`ZombieBoss` / `ZombieBossDave` / `ZombieBossEdgarII`。
	/// 它们走的是标准实例化路径（`PacketConfig.Create` → `TowerDefenseManager.CreateCharacter`
	/// → BOSS 自己的 `_Ready()` / `InitializeBossGameplay()`），所以**能正常生成**。
	/// 但 BOSS 血量极高、还会召唤小怪，随机开出来可能直接毁掉一局。
	///
	/// true  = 允许（默认，符合「完全随机」的设定）
	/// false = 把 BOSS 从池子里剔除
	/// </summary>
	private const bool AllowBossZombie = true;

	/// <summary>
	/// ★ 是否允许抽出「巨人僵尸 / 红眼巨人」（Gargantuar 系列，池子里共 30 种）。
	///
	/// 同理：能正常生成，但强度远高于普通僵尸。
	/// </summary>
	private const bool AllowGargantuar = true;

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
			Log("初始化完成；PackageRoot=" + root + "。种下后将从植物+僵尸混合卡池里随机开出。");
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
		}
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
			if (!_bankOk)
			{
				_bankOk = EnsureMixedBank();
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
			// 场上有模仿者时，逐帧把「免咬」开关按回可被咬的取值
			// （引擎进旋转状态会自己设无敌/免咬，只在 _Ready 写一次会被覆写）
			RandomImitater.TickAllBiteable();
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
		return gotMine;
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
						if (_groupOriginal.TryGetValue(btn.GetInstanceId(), out d) && d != null)
						{
							d.DynamicInvoke(new object[] { id });
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
		Delegate d = _reselectOriginal;
		if (d != null)
		{
			try
			{
				d.DynamicInvoke(new object[0]);
				return;
			}
			catch (Exception ex)
			{
				// ★ 底稿可能是**上一个场景**留下的、目标已被释放的委托（换关 / 重进选卡界面），
				//   这时 DynamicInvoke 会抛。绝不能就此收手 —— 下面还有复刻版兜底。
				Swallow("调用原生「重选」底稿", ex);
			}
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
				return true;
			}

			var plantList = new Godot.Collections.Array();
			var zombieList = new Godot.Collections.Array();
			var seen = new HashSet<string>(StringComparer.Ordinal);
			int skipped = 0;

			// ① 首选：具名卡池（和以前一样，保证卡池构成、概率与今天完全一致）。
			TowerDefensePacketBankData plantSrc = TowerDefenseManager.GetPacketBankData(PlantSrcBank);
			if (plantSrc != null && GodotObject.IsInstanceValid(plantSrc))
			{
				foreach (string cat in new[] { "White", "Gold", "Diamond", "Colour", "Star", "Original" })
				{
					Collect(plantSrc.GetCategory(cat), plantList, seen, wantZombie: false, ref skipped);
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
			Godot.Collections.Array mixed = BuildWeighted(plantList, zombieList, PlantChance,
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

			int plantEntries = plantList.Count * repP;
			int zombieEntries = zombieList.Count * repZ;
			double realChance = mixed.Count > 0 ? (double)plantEntries / mixed.Count : 0.0;
			Log("已注册卡池 " + CustomBank + "：植物 " + plantList.Count + " 张 + 僵尸 "
				+ zombieList.Count + " 只；每张植物 x" + repP + "、每只僵尸 x" + repZ
				+ " ⇒ 列表 " + mixed.Count + " 条，实际出植物概率 "
				+ (realChance * 100).ToString("0.###") + "%（目标 "
				+ (PlantChance * 100).ToString("0.#") + "%）；同类卡等概率；跳过 " + skipped + "；"
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
	/// 实测（植物 325 / 僵尸 290、PlantChance = 0.65）：最优解 a=58、b=35，
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

	/// <summary>按开关过滤僵尸：BOSS / 巨人系列可分别关掉。</summary>
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
	/// </summary>
	private void RiExplode()
	{
		try
		{
			// ① 腾位置：同步清掉 slot / characterSlotDictionary / characterList
			//    （freeInstance = false ⇒ 不立即 QueueFree，节点还要用于收尾；与官方一致）
			Destroy(false);

			// ② 抽卡 + 落点校验 + 种下/生成，全部交给基类原逻辑：
			//    它读的就是本 Mod 运行时注册的混合卡池 packetBank，行为与今天完全一致，
			//    只是现在校验看到的是**空格子**，植物不会再被自己挡掉。
			base.Explode();
		}
		catch
		{
			// 不许抛：变身回调里抛出会让战斗流程崩。
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
