# SP2 式"异步 prefab 预加载 + 真实百分比加载框"——消除加入时的白屏卡顿

状态:✅ 已归档(代码已实现,MSBuild 编译 exit 0;游戏内联机实测见"验证 3/4",按需复跑)。
日期:2025 (session checkpoint 后)

## 〇、经验教训(归档修订)

> 本 plan 已按改动清单 1~5 全部落地并归档为开发经验记录。**未勾选项 = 未留档的待验证项**:游戏内实测(大飞船白框 + 百分比递增、踢人/离开/断开无残留)需按"验证"第 3/4 条复跑,勿当作当前待办执行。

**结论**:`MpCraftPreloader.cs`(`PreloadCraftPrefabs` 协程 + `MpCraftLoadingIndicator`)+ `MpNetworkManager` 生成链路改造(预加载 → SpawnCraft)+ `MultiPlayerUI` 玩家列表 "⏳ N%" 已完成;`Resources.LoadAsync` 与 `Resources.Load` 共享缓存,主 prefab 热缓存后 SpawnCraft 只剩纯 Instantiate,消除秒级白屏。

**经验教训**:

1. **路径一致性命门**:预热路径必须与 `CraftBuilder.CreatePartGameObject`(`.prefab` 后缀去除 + `Assets/` 前缀判定)完全一致,否则缓存不命中、预热无效(无害但白做)。
2. **预加载是长时间协程 → 必须防重复**:数秒预加载期间状态包 20Hz 到达,若不做 `_pendingSpawns` 去重,每个状态包都会再起一个生成协程重复预加载同一飞船;预加载期间用 `_pendingSpawnLatest` 只刷新最新状态,生成时用最新位置减少跳变。
3. **节流与去重顺序**:去重检查要放在 `_spawnAttemptTime` 节流(2s)之前,否则预加载超过 2s 后最新状态停止刷新。
4. **清理点全覆盖**:预加载可能被"踢人/离开/断开/场景切换/销毁"任一路径打断,进度框与挂起状态在每个清理点(`RemoveRemoteCraft`/`Stop`/`OnFlightSceneLoaded`/`OnDestroy`)都要销毁,否则残留。
5. **不碰装配链**:只预热缓存、不改写 `SpawnCraft`/modifier 加载,游戏更新即碎的整机装配逻辑保持原样。
6. **加载框朝向**:用"拷贝相机旋转 + 薄板绕视线轴(Z)旋转"比 `LookAt` + 绕 Y 更接近 SP2 的旋转方框观感;`TextMesh` 在 2022.3 可用,无需 TMP。

## 目标
远程玩家飞船生成时,把目前"同步加载全部部件 prefab → 秒级白屏"改为"**异步预加载 + 真实百分比加载框 → 快速生成**",消除加入时的白屏,并让玩家看到"XX 正在加载飞船 N%"。

## 原理(已用反编译证据确认可行)
- JNO 白屏大头 = `SpawnCraft → CraftBuilder.CreatePartGameObjects → 每部件 ResourceLoader.InstantiatePrefab(PartType.PrefabPath)`(同步、原子,无 yield 点,无法在中间拆帧)。
  - 证据:`CraftBuilder.cs:270` `CraftBuilder.CreatePartGameObjects(craftScript.Data.Assembly.Parts, craftScript)`;`CreatePartGameObject`(行 314,内部 `InstantiatePrefab(part.PartType.PrefabPath)`);`PartType.PrefabPath` 公开(ModApi/PartType.cs:351)。
  - `CraftData` 构造是纯数据(`new Assembly(xml,...)`),不实例化 GameObject。
- JNO 有异步原语 `ResourceLoader.LoadAsync<T>`(包 `Resources.LoadAsync`,返回 `ResourceRequestWrapper<T>`,可查 `Request.isDone/progress/asset`)。
  - 证据:`ResourceLoader.cs:97`;`ModApi/Common/ResourceRequestWrapper.cs`。
- 关键:`Resources.LoadAsync` 与 `Resources.Load` **共享同一缓存** → 先 `LoadAsync` 读进缓存,再让游戏自己的原子 `SpawnCraft` 照常跑 → 主 prefab 命中缓存,只剩纯 Instantiate,白屏大头消除。
- **不拦截/不改写/不跳过任何装配逻辑**:modifier 子 prefab(轮胎/座椅/MFD)仍按原链同步加载,正常显示(已确认)。

## 改动清单

### 1. 新建 `Assets/Scripts/Net/MpCraftPreloader.cs`(协程预加载器)
- 静态方法/协程:`PreloadCraftPrefabs(CraftData craftData, Action<float> onProgress, Action onDone)`:
  - 收集 `craftData.Assembly.Parts` 每个 `PartData.PartType.PrefabPath`(去重)。
  - **路径处理(关键)**:主游戏部件按 `CraftBuilder.cs:332` 一致地去掉 `.prefab` 后缀,`ResourceLoader.LoadAsync<GameObject>(path)` 逐帧等 `Request.isDone`,完成一个报一次 `onProgress(已加载/总数)`;
  - **mod 部件**(`PartType.Mod != null && PrefabPath.StartsWith("Assets/")`):走 `mod.ResourceLoader.LoadAsset<GameObject>` 同步加载(数量少,分帧散开)。
  - 失败容错:某路径加载失败记日志、继续,不阻塞整体。
- 可取消(`Stop()` 标记),玩家离开/场景切换时停止。

### 2. 加载进度框 UI(SP2 风格)
- 在远程玩家首个状态包位置上方挂一个**旋转的白色小方框**(`GameObject.CreatePrimitive(Cube)` 压扁成薄板),子物体挂 `TextMesh`(Unity 内置)显示 `"N%"`(真实进度)。
- 始终面向相机(billboard:每帧 `LookAt(camera)` 或绕 Y 轴旋转如 SP2 `LoadingAircraftStatusScript`)。
- 加载完成销毁。实现时验证 `TextMesh` 可用;不可用则改用 TextMeshPro。

### 3. 改造 `MpNetworkManager.SpawnRemoteCraftCoroutine` / `SpawnRemoteCraftAtPosition`
新链路(在现有 2 帧延迟协程内):
```
yield null ×2                      # 现有:让握手/状态包先流动
XElement.Parse(peer.CraftXml)      # 帧 A
CraftData cd = new CraftData(...)  # 帧 B(纯数据,快)
创建进度框 → 挂到对方位置
yield 预加载主 prefab(逐帧,报真实 %)  → 进度框显示 N%
SpawnCraft(cd, location, xml)      # 主 prefab 已热缓存 → 快速
销毁进度框
ApplyRemoteState + 登记 RemoteCraft + 幻影模式(现有逻辑原样)
```
- 失败/取消:玩家离开、场景切换、协程中断时销毁进度框、停止预加载(现有 `peer`/`_playersByPlayerId` 校验保留)。
- 保留现有"首个状态包位置生成""表面锁定""幻影模式""ApplyRemoteState"全部逻辑不动。

### 4. 本地化(EN-US.xml / ZH-CN.xml 各 +1 键)
- `LoadingCraft`:EN "Loading craft" / ZH "正在加载飞船"
- 百分比文本直接用字符串拼 `"{0}%"`,不必单独建键。

### 5. (可选)玩家列表该行加"加载中"状态
- 预加载期间该玩家行的 valueGetter 返回 `"⏳ N%"`,完成后恢复延迟显示。若实现复杂则跳过。

## 明确不做
- 不重写 JNO 的整机装配(部件/body/关节/质心/modifier/飞行状态接线)——风险高,游戏更新即碎。
- 不改联机协议、不改 IMpTransport、不加消息类型。
- 不动 modifier 子 prefab 的加载逻辑(保持正常显示)。

## 验证
1. MSBuild 编译通过(exit 0)。
2. 两个语言文件 UTF-8 校验。
3. 游戏内联机实测:大飞船加入时,对方位置出现旋转白框 + 真实百分比递增,不再秒级白屏;对方飞船生成后位置/朝向/尾焰正常(复用现有断言与日志)。
4. 回归:踢人/离开/断开时进度框能正确销毁,无残留。

## 风险与边界
- 主 prefab 预热只解决"磁盘读 + 反序列化"大头;剩下纯 Instantiate + 装配仍占少量时间,极端大船仍可能有短卡顿(可感知、非秒级白屏)。
- modifier 子 prefab(轮胎/座椅/MFD)仍同步现场加载——通常轻量、常被内嵌跳过;极端 mod 载具会留一点同步时间(已确认不影响显示)。
- `Resources.LoadAsync` 与 mod 的 `LoadAsset` 缓存体系不同,mod 部件另处理。
- **路径一致性命门**:预热路径必须与 `CraftBuilder` 去掉 `.prefab` 后缀的用法一致,否则缓存不命中、预热无效(无害但白做)。

## 交付物
- `Assets/Scripts/Net/MpCraftPreloader.cs`(新)
- `Assets/Scripts/Net/MpNetworkManager.cs`(改造生成链路)
- `Assets/Content/Languages/EN-US.xml` / `ZH-CN.xml`(+键)
- 若做第 5 项:`Assets/Scripts/MultiPlayerUI.cs`(玩家列表"加载中"状态)

## 实现记录(2025,本次落地)

- ✅ 第 1~5 项全部落地,`aMptest.csproj` MSBuild 编译 exit 0,EN/ZH 语言文件 XML+UTF-8 校验通过。
- `MpCraftPreloader.cs`:静态协程 `PreloadCraftPrefabs(craftData, onProgress, isCancelled)` + `MpCraftLoadingIndicator`(billboard 旋转白框 + TextMesh,文案取 `MultiPlayer.MultiPlayerUI.LoadingCraft` + `N%`)。
- `MpNetworkManager.cs`:
  - `SpawnRemoteCraftCoroutine` 改为预加载链路(2 帧 → 解析 XML/构建 CraftData → 建进度框 → 逐帧预加载报真实 % → SpawnCraft → 销毁进度框);`SpawnRemoteCraftAtPosition` 接收预构建的 craftData/location/xml。
  - 新增 `_pendingSpawns` 去重:预加载数秒期间状态包只刷新 `_pendingSpawnLatest`(生成时用最新位置,减少跳变),不重复起协程。
  - 新增 `IsSpawnAttemptStillValid` / `EndSpawnAttempt` / `CancelPendingSpawns` / `CreateLoadingIndicator` / `DestroyLoadingIndicator` / `GetPlayerLoadProgress`;清理点:踢人(`RemoveRemoteCraft`)、断开(`Stop`)、场景切换(`OnFlightSceneLoaded`)、销毁(`OnDestroy`)。
- `MultiPlayerUI.cs`:玩家列表行预加载期间 valueGetter 返回 `"⏳ N%"`,完成后恢复原显示。
- 待游戏内实测:大飞船加入时对方位置出现旋转白框 + 百分比递增、不再秒级白屏;踢人/离开/断开无进度框残留(对应上方"验证"第 3、4 条)。
