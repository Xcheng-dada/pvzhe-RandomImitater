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
	/// ★ 「重选上次卡牌」补齐重复卡：让本卡的 5 张（或任意多张）能被完整记忆/恢复。
	///
	/// 塌陷点（IL 证据，见分析报告）：
	///   写入侧 <c>EmitChooseOverAsync</c> 是 foreach + <c>Array.Add</c>，**不去重**，
	///   5 张本卡会原样写成 5 个重复串 —— 存档是好的。
	///   读取侧 <c>ReSelectButtonPressed</c> → <c>DeleteAllPacket()</c> → <c>PacketListChoose()</c>，
	///   而 <c>PacketListChoose</c> 里 IL_0077 <c>seedBank.HasPacket(name)</c> + IL_007C <c>brtrue</c>
	///   把第 2..5 个同 key 的条目**直接 continue 掉**（<c>HasPacket</c> 查的是
	///   <c>packetNameSet</c> 这个当 HashSet 用的字典，5 张只有 1 个 key）。
	/// ⇒ 所以「选了 5 张随机模仿者，重选只回来 1 张」。
	///
	/// 修法（方案 A，只动本 Mod 自己的卡，不碰任何现有内容）：
	///   在原生 <c>ReSelectButtonPressed</c> **之后**再挂一个处理器（signal 按连接顺序执行），
	///   读同一份存档数组数出「本卡需要 N 张」，数出卡槽里「已有 M 张」，差额用原生
	///   <c>CreateAnime</c> 补上（自带飞入动画；它内部 IL_00BA 直接调 <c>AddPacket</c>，
	///   而 <c>AddPacket</c> 自身**没有** HasPacket 去重守卫 ⇒ 可以补重复卡）。
	///   补之前照例判 <c>CanAddPacket()</c>（槽位总数上限仍由引擎把着）。
	/// </summary>
	private static readonly bool ReselectTopUp = false;

	/// <summary>存档里「上次卡牌选择」的键名（与游戏 <c>EmitChooseOverAsync</c> 用的串一致）。</summary>
	private const string ReSlectKey = "PacketReSlect";

	/// <summary>最近一次扫到的本卡池卡（补齐时借用它的位置做飞入动画起点）。</summary>
	private TowerDefenseInGamePacketShow _myPoolCard;

	/// <summary>
	/// 「重选」按下后的等待帧数。>0 表示引擎自己的 DeleteAllPacket + PacketListChoose
	/// 可能还没跑完，先别动；归零那一帧才做补齐。
	/// </summary>
	private int _reselectWaitFrames;

	/// <summary>日志去重标记。</summary>
	private bool _reselectLogged;
	private bool _reselectErrLogged;

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
			if (ReselectTopUp)
			{
				DriveReselectHook();
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

	// ================================================================ 重选记忆（补齐重复卡）

	/// <summary>
	/// 每帧看一眼选卡界面的「重选上次卡牌」按钮，把我们的补齐处理器挂到它的
	/// `OnPressed` 上。
	///
	/// ★ 挂在按钮上而不是直接改 `ReSelectButtonPressed`：本 Mod 不碰游戏任何现有逻辑，
	///   点一次「重选」多走一次我们自己的补齐；卸载 Mod 后按钮行为原样。
	/// ★ 处理器本身**只立个标记**，真正的补齐等到下一帧 `process_frame` 才做。
	///   这样即使游戏之后又把它的处理器 Combine 到我们后面，补齐也一定发生在
	///   「原生清空 + 恢复」整体结束之后 —— 不依赖处理器先后顺序。
	/// </summary>
	private void DriveReselectHook()
	{
		// 「重选」按下后等 2 帧：让引擎的 DeleteAllPacket() + PacketListChoose() 先跑完，
		// 再一次性补齐。只补一次，不会像之前那样反复触发几十次。
		if (_reselectWaitFrames > 0)
		{
			_reselectWaitFrames--;
			if (_reselectWaitFrames == 0)
			{
				TopUpReselect();
			}
			return;
		}
		if (InfiniteSelect && _mineSeen == 0)
		{
			// 无限选取那套已经逐帧扫过卡片节点了，用它的结果当门，省掉平时的节点查找。
			// （InfiniteSelect 关掉时不能靠这个门 —— 那时 _mineSeen 永远是 0，
			//   就自己每帧查一次按钮，开销只是几个反射 + 一次 GetNodeOrNull。）
			return;
		}
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
			//   游戏在 `_ConnectPacketBankSignals` 里可能重建按钮并重新 Combine（换场景 / 换分类），
			//   我们的处理器会随之丢掉 —— 记账式会漏挂，读活委托能自愈。
			//   反过来也**不能**每帧无脑追加：那会让一次点按补好几轮。
			object cur = GetEventField(btn, "OnPressed");
			if (IsMine(cur, nameof(OnReSelectPressed)))
			{
				return;   // 已经挂过了
			}
			MethodInfo handler = typeof(RandomImitaterEntry).GetMethod(
				nameof(OnReSelectPressed),
				BindingFlags.NonPublic | BindingFlags.Instance);
			if (handler == null)
			{
				return;
			}
			if (!AppendEventField(btn, "OnPressed", handler))
			{
				return;
			}
			if (!_reselectLogged)
			{
				_reselectLogged = true;
				Log("已挂上「重选上次卡牌」补齐处理器（重复的本卡不再被去重丢掉）。");
			}
		}
		catch (Exception ex)
		{
			if (!_reselectErrLogged)
			{
				_reselectErrLogged = true;
				Log("挂「重选」补齐处理器失败（已吞）：" + ex.Message);
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

	/// <summary>按钮按下：只排一个「两帧后补齐」，真正的补齐放到引擎恢复完之后。</summary>
	private void OnReSelectPressed()
	{
		_reselectWaitFrames = 2;
	}

	/// <summary>
	/// 「重选」之后补齐本卡的重复份数。
	///
	/// 原生 `PacketListChoose` 用 `seedBank.HasPacket(key)`（查 `packetNameSet` 这个当
	/// HashSet 用的字典）做早退，同 key 的第 2..N 份被跳过 ⇒ 5 张只回来 1 张。
	/// 存档数组本身是好的（写入侧纯 Append 不去重），所以这里照它数差额：
	///   需要 N = 存档数组里 key == 本卡 的条目数
	///   已有 M = seedBank.packetList 里 key == 本卡 的条目数
	///   补 N-M 张（直接 `seedBank.AddPacket(cfg, false)`，不走 Tween）
	///
	/// 用 `AddPacket` 而不是 `bank.CreateAnime` 的原因（之前版本踩过的两个坑）：
	///   ① `CreateAnime` 通过 Tween 把卡片排队飞入，`packetList.Add` 在 Tween 回调里才发生；
	///      一次发 4 次 `CreateAnime`，下一帧 `OnFrame` 只看到 1 张入槽 ⇒ 循环立刻退出、
	///      剩 3 张永远不补 —— 这就是「点一次只补 1 张、第二次才补剩下 4 张」的根因。
	///   ② `CreateAnime` 每次只 Append 到 `packetList` 末尾 ⇒ 补齐卡排在原生 1 张之后、
	///      整体挤到玩家之前选的非本卡后面 —— 这就是「顺序变了」的根因。
	/// `AddPacket(cfg, false)` 一次性同步把卡片放进 `packetList` + 分配 slot（IL_00E1
	/// `packetList.Add` + IL_00F6 `EnsurePacketSlots()`），后续帧立即可见；它内部就是
	/// 排在当前 `packetList` 末尾，所以补齐卡依然紧跟在原生那 1 张后面 —— 这正好
	/// 对应「玩家之前选的非本卡在前、然后 5 张本卡」的选择顺序。
	///
	/// 只做本卡，别的卡一张都不碰 ⇒ 不改动任何现有内容。
	/// 受引擎自身的 `CanAddPacket()`（`packetNum &lt; seedbankPacketMax`）约束，槽位满了自然停。
	/// </summary>
	private void TopUpReselect()
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

			// ① 存档里本卡需要几张
			int need = 0;
			if (GameSaveManager.Instance == null
				|| !GodotObject.IsInstanceValid(GameSaveManager.Instance))
			{
				return;
			}
			Variant stored;
			try
			{
				stored = GameSaveManager.Instance.GetKeyValue(ReSlectKey);
			}
			catch
			{
				return;
			}
			Godot.Collections.Array saved = stored.AsGodotArray();
			if (saved == null)
			{
				return;
			}
			foreach (Variant v in saved)
			{
				if (string.Equals(v.AsString(), MyKey, StringComparison.Ordinal))
				{
					need++;
				}
			}
			if (need <= 1)
			{
				return;   // 只选了一张（或没选）：原生恢复的就是对的
			}

			// ② 卡槽里现在实际有几张本卡
			int have = 0;
			Godot.Collections.Array<TowerDefenseInGamePacketShow> list = seedBank.packetList;
			if (list != null)
			{
				for (int i = 0; i < list.Count; i++)
				{
					TowerDefenseInGamePacketShow c = list[i];
					if (c != null && GodotObject.IsInstanceValid(c) && IsMyCard(c))
					{
						have++;
					}
				}
			}
			if (have >= need)
			{
				return;   // 已经齐了（重复点「重选」时不会越补越多）
			}

			// ③ 补差额
			TowerDefensePacketConfig cfg = TowerDefenseManager.GetPacketConfig(MyKey);
			if (cfg == null || !GodotObject.IsInstanceValid(cfg))
			{
				return;
			}

			int added = 0;
			for (int i = have; i < need; i++)
			{
				if (!seedBank.CanAddPacket())
				{
					break;   // 卡槽已满（引擎自己的上限）
				}
				// 同步入槽：内部会 packetList.Add + EnsurePacketSlots，下一帧立即可见。
				// 第二个参数 false = "not in combat"（与原生恢复路径一致：
				// `PacketListChoose` → `CreateAnime` → `AddPacket(cfg, IsGameRunning())`，
				// 而我们在选卡界面、IsGameRunning() 是 false）。
				TowerDefenseInGamePacketShow addedCard = seedBank.AddPacket(cfg, false);
				if (addedCard == null || !GodotObject.IsInstanceValid(addedCard))
				{
					break;   // 守卫没过（packetContainer 还没 Ready 等），不再继续
				}
				// ★ 关键：AddPacket(cfg, false) 只把卡塞进 packetList 并分配 slot，
				//   **不会** StartInit()、也**不会**置 alive = true。这两步在原生路径里是
				//   飞入动画结束时由 CompletePacketAnimation 补的。少了它们，卡片虽然在
				//   packetList 里、占了槽位，但没初始化 ⇒ 表现就是「补了却还是只能选一张」。
				try { addedCard.StartInit(); } catch { }
				try { addedCard.alive = true; } catch { }
				added++;
			}

			if (added > 0)
			{
				Log("「重选」补齐：" + MyKey + " 记忆 " + need + " 张、原生恢复 " + have
					+ " 张、补上 " + added + " 张"
					+ ((added < need - have) ? "（未满额：卡槽上限）" : "") + "。");
			}
		}
		catch (Exception ex)
		{
			if (!_reselectErrLogged)
			{
				_reselectErrLogged = true;
				Log("「重选」补齐异常（本条只报一次）：" + ex.Message);
			}
		}
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
