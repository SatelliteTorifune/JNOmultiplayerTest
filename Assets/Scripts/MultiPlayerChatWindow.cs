using System;
using System.Collections;
using System.Text;
using Assets.Scripts.Net.Session;
using ModApi;
using ModApi.Ui;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Assets.Scripts
{
	/// <summary>
	/// 文字聊天 UI(XmlLayout 原生窗口,2026-09-24 路线 B;plans/text-chat-2026-09-24.md §四 M2')。
	/// 取代 IMGUI 版(已删除;外观与游戏 UI 割裂被否)。XML 用游戏原生 class/prefab(FlightStyles.xml),
	/// 挂在游戏全局 UI 根(<c>BuildUserInterfaceFromXml</c> 默认 parent = UserInterface.Transform)。
	/// 三态:
	/// - Hidden:未连接 / 不在飞行场景(窗口与预览条都收);
	/// - Collapsed(默认):左下角预览条,最新一条 200 字截断 + 5 秒淡出(SP2 同款),点击展开;
	/// - Expanded:日志(VerticalScrollView + 单个 TMP 富文本块)+ 单行输入框 + 发送按钮 + × 关闭;
	///   Enter 提交(onSubmit)、提交后清空输入并保持焦点(SP2 连续输入形态)。
	/// 输入框是 uGUI/TMP ⇒ 聚焦时游戏 <c>IsTextInputFocused</c> 成立 ⇒ **打字自动屏蔽飞行控制**(§3.3 机制),
	/// IMGUI 版的"打字触发飞行控制/Esc 弹菜单"两个已接受缺陷在本路线自动消失。
	/// RTF:显示层(TMP)默认不解析富文本,建窗后显式打开(**玩家可发 RTF**,【决策:2026-09-24】);
	/// **玩家名同样允许 RTF**(自带标签时不再套显示层配色,避免标记冲突),日志与预览条都启用 richText。
	/// 入口:飞行 ViewPanel 聊天按钮(Mod.OnChatUiButtonClicked → <see cref="Toggle"/>)。
	/// </summary>
	public class MultiPlayerChatWindow : MonoBehaviour
	{
		public static MultiPlayerChatWindow Instance { get; private set; }

		private const string UserInterfaceId = "MultiPlayerChatWindow";
		private const int PreviewMaxChars = 200;    // SP2 同款:预览截断 200 字
		private const float PreviewVisibleSec = 5f; // SP2 同款:预览 5 秒后淡出
		private const int MaxLines = 100;           // 与 ChatSession 环形缓冲一致

		private IXmlLayout _layout;
		private IXmlElement _windowElement;
		private IXmlElement _previewElement;
		private TextMeshProUGUI _logText;
		private TextMeshProUGUI _previewLabel;
		private RectTransform _contentRect;
		private ScrollRect _scroll;
		private TMP_InputField _input;
		private ChatSession _chat;
		private bool _isBound;
		private bool _expanded;
		private float _previewHideAt = -1f;

		private void Awake() { Instance = this; }

		private void OnDestroy()
		{
			if (Instance == this) Instance = null;
			if (_chat != null) _chat.MessageReceived -= OnChatMessageReceived;
		}

		/// <summary>
		/// ViewPanel 聊天按钮入口:未连接或不在飞行场景时不生效(不建窗)。
		/// 窗口 GameObject 随场景销毁时(Unity 假 null)自动重建 —— 首次点击/首次消息都会走这里。
		/// </summary>
		public static void Toggle()
		{
			if (CurrentChat() == null) return;
			MultiPlayerChatWindow w = Instance;
			if (w == null) w = Build();
			if (w == null) return;
			w.ToggleExpanded();
		}

		/// <summary>XML 建窗(游戏原生 API):T = 本控制器,layoutRebuilt 回调里绑定元素与事件。</summary>
		private static MultiPlayerChatWindow Build()
		{
			try
			{
				return Game.Instance.UserInterface.BuildUserInterfaceFromXml<MultiPlayerChatWindow>(
					BuildXml(), UserInterfaceId, (script, controller) => script.Bind(controller.XmlLayout));
			}
			catch (Exception e)
			{
				Mod.LogError("MultiPlayerChatWindow: build failed: " + e);
				return null;
			}
		}

		/// <summary>
		/// 窗口 XML(游戏原生 class/prefab;文案走 Locale §九.5)。
		/// 事件不在 XML 里写 onClick,统一在 <see cref="Bind"/> 里用 AddOnClickEvent 挂(仓库已验证的接线方式)。
		/// 尺寸(2026-09-24 放宽):窗 520×330、日志区高 240、输入行高 42(输入框 420×34)。
		/// </summary>
		private static string BuildXml()
		{
			string title = Locale.GetString("MultiPlayer.Chat.WindowTitle");
			string send = Locale.GetString("MultiPlayer.Chat.Send");
			string placeholder = Locale.GetString("MultiPlayer.Chat.InputPlaceholder");
			return $@"<XmlLayout xmlns='{XmlLayoutConstants.XmlNamespace}'>
  <Include path='Ui/Xml/Flight/FlightStyles.xml' />
  <Panel id='mp-chat-preview' class='translucent-panel-dark border' rectAlignment='LowerLeft' pivot='0 0' offsetXY='16 16' width='500' height='26' raycastTarget='true' showAnimation='FadeIn' hideAnimation='FadeOut' animationDuration='0.15' active='false'>
    <TextMeshPro id='mp-chat-preview-text' class='value' alignment='Left' margin='8 0 8 0' overflowMode='Ellipsis' richText='true' />
  </Panel>
  <VerticalLayout id='mp-chat-window' class='translucent-panel-dark border draggable' translucency='0.1' rectAlignment='LowerLeft' pivot='0 0' offsetXY='16 16' width='520' height='330' spacing='0' padding='0' childForceExpandHeight='false' childForceExpandWidth='true' showAnimation='FadeIn' hideAnimation='FadeOut' animationDuration='0.15' active='false'>
    <Panel class='inspector-header' color='DarkPanel' translucency='0' preferredHeight='26'>
      <TextMeshPro id='mp-chat-title' class='inspector-title' text='{title}' alignment='Left' margin='8 0 40 0' />
    </Panel>
    <VerticalScrollView class='no-image' id='mp-chat-scroll' preferredHeight='240' pivot='0 1'>
      <Panel id='mp-chat-content' width='100%' height='240'>
        <TextMeshPro id='mp-chat-log' width='100%' height='240' margin='6 6 6 6' alignment='UpperLeft' richText='true' />
      </Panel>
    </VerticalScrollView>
    <HorizontalLayout preferredHeight='42' spacing='6' padding='6' childForceExpandHeight='false'>
      <TextMeshProInputField id='mp-chat-input' text='' lineType='SingleLine' characterLimit='500' width='440' height='34' fontSize='18'>
        <TMP_Placeholder id='mp-chat-placeholder' text='{placeholder}' alignment='Left' />
        <TMP_Text alignment='Left' />
      </TextMeshProInputField>
      <Button id='mp-chat-send' class='btn btn-primary' width='56' height='34' raycastTarget='true'>
        <TextMeshPro id='mp-chat-send-text' text='{send}' />
      </Button>
    </HorizontalLayout>
    <ContentButton id='mp-chat-close' class='btn audio-btn-click' ignoreLayout='true' rectAlignment='UpperRight' pivot='1 1' offsetXY='-4 -4' width='24' height='24' raycastTarget='true'>
      <Image sprite='Ui/Sprites/Common/IconCloseFlyout' width='12' height='12' />
    </ContentButton>
  </VerticalLayout>
</XmlLayout>";
		}

		/// <summary>布局建好后绑定元素与事件(每次建窗调一次)。</summary>
		private void Bind(IXmlLayout layout)
		{
			_layout = layout;
			_windowElement = layout.GetElementById("mp-chat-window");
			_previewElement = layout.GetElementById("mp-chat-preview");
			_logText = layout.GetElementById<TextMeshProUGUI>("mp-chat-log");
			_previewLabel = layout.GetElementById<TextMeshProUGUI>("mp-chat-preview-text");
			_scroll = layout.GetElementById<ScrollRect>("mp-chat-scroll");
			_input = layout.GetElementById<TMP_InputField>("mp-chat-input");
			IXmlElement content = layout.GetElementById("mp-chat-content");
			_contentRect = content != null ? content.RectTransform : null;

			IXmlElement close = layout.GetElementById("mp-chat-close");
			if (close != null) { EnsureRaycastable(close); close.AddOnClickEvent(Collapse); }
			if (_previewElement != null) { EnsureRaycastable(_previewElement); _previewElement.AddOnClickEvent(OnPreviewClicked); }
			IXmlElement send = layout.GetElementById("mp-chat-send");
			if (send != null) { EnsureRaycastable(send); send.AddOnClickEvent(OnSendClicked); }
			if (_input != null) _input.onSubmit.AddListener(OnInputSubmitted); // Enter 提交

			// XmlLayout 的 TMP 默认不解析富文本(游戏 XML 里凡要富文本的元素都显式写 richText="true")⇒
			// 名字配色/系统消息样式/玩家 RTF 都依赖它,元素属性之外再在代码里兜底打开(预览条同理)。
			if (_logText != null) _logText.richText = true;
			if (_previewLabel != null) _previewLabel.richText = true;

			_isBound = true;
			Mod.LogLobby("MultiPlayerChatWindow: built (window=" + (_windowElement != null) + ", preview=" + (_previewElement != null) +
				", log=" + (_logText != null) + ", scroll=" + (_scroll != null) + ", input=" + (_input != null) +
				", close=" + (close != null) + ", send=" + (send != null) + ")");
		}

		/// <summary>
		/// 保证元素吃射线(uGUI 点击的前提):XML 属性不一定落到组件上 —— 实测"右上角 × 点不动"即此因
		/// (游戏自己的关闭键在 Styles.xml 里也是显式 `raycastTarget="true"`)。建窗后统一在代码里兜底打开。
		/// </summary>
		private static void EnsureRaycastable(IXmlElement element)
		{
			if (element == null || element.GameObject == null) return;
			Graphic graphic = element.GameObject.GetComponent<Graphic>();
			if (graphic != null) graphic.raycastTarget = true;
		}

		/// <summary>当前可用聊天会话:已连接 + 在飞行场景才显示(否则视为 Hidden)。</summary>
		private static ChatSession CurrentChat()
		{
			NetworkManager nm = NetworkManager.Instance;
			if (nm == null || !nm.IsConnected) return null;
			if (Game.Instance == null || !Game.Instance.SceneManager.InFlightScene) return null;
			return nm.Chat;
		}

		private void Update()
		{
			ChatSession chat = CurrentChat();
			if (!ReferenceEquals(_chat, chat))
			{
				if (_chat != null) _chat.MessageReceived -= OnChatMessageReceived;
				_chat = chat;
				if (_chat != null) _chat.MessageReceived += OnChatMessageReceived;
				else Collapse(); // 断线 / 离开飞行场景:全部收起
			}
			if (_previewHideAt > 0f && Time.unscaledTime >= _previewHideAt)
			{
				_previewHideAt = -1f;
				if (_previewElement != null) _previewElement.Hide(); // 5 秒到 → FadeOut 动画
			}
		}

		// ---------------- 三态 ----------------

		public void ToggleExpanded()
		{
			if (_expanded) Collapse();
			else Expand();
		}

		private void Expand()
		{
			if (!_isBound) return;
			_expanded = true;
			_previewHideAt = -1f;
			if (_previewElement != null) _previewElement.Hide();
			if (_windowElement != null) _windowElement.Show();
			RebuildLog();
			RefocusInput();
		}

		private void Collapse()
		{
			_expanded = false;
			if (_windowElement != null) _windowElement.Hide();
			if (_previewElement != null) _previewElement.Hide();
		}

		private void OnPreviewClicked()
		{
			if (CurrentChat() == null) return;
			Expand();
		}

		// ---------------- 消息渲染 ----------------

		private void OnChatMessageReceived(ChatMessage msg)
		{
			// 传输层 DrainIncoming 在 NetworkManager.Update 泵包 → 事件在主线程,可直接碰 Unity API
			if (_expanded)
			{
				RebuildLog();
				return;
			}
			if (_previewLabel != null) _previewLabel.text = ClampForPreview(FormatPlain(msg));
			if (_previewElement != null) _previewElement.Show(); // FadeIn
			_previewHideAt = Time.unscaledTime + PreviewVisibleSec;
		}

		/// <summary>整块日志重建:单 TMP 富文本块(每行一条,系统消息灰斜体、名字上色)+ 贴底。</summary>
		private void RebuildLog()
		{
			if (_logText == null) return;
			var messages = _chat != null ? _chat.Messages : null;
			if (messages == null || messages.Count == 0)
			{
				_logText.text = string.Empty;
				ResizeLogAndScrollToBottom();
				return;
			}
			int start = Mathf.Max(0, messages.Count - MaxLines);
			var sb = new StringBuilder();
			for (int i = start; i < messages.Count; i++) sb.Append(FormatLine(messages[i])).Append('\n');
			_logText.text = sb.ToString();
			ResizeLogAndScrollToBottom();
		}

		/// <summary>文本按内容高度撑开(不依赖 contentSizeFitter 猜测),并把滚动条拉到底。</summary>
		private void ResizeLogAndScrollToBottom()
		{
			if (_logText == null) return;
			float h = Mathf.Max(24f, _logText.preferredHeight + 12f);
			RectTransform textRect = _logText.rectTransform;
			textRect.sizeDelta = new Vector2(textRect.sizeDelta.x, h);
			if (_contentRect != null) _contentRect.sizeDelta = new Vector2(_contentRect.sizeDelta.x, h);
			UnityEngine.Canvas.ForceUpdateCanvases();
			if (_scroll != null) _scroll.verticalNormalizedPosition = 0f; // 0 = 底部
		}

		/// <summary>整行渲染(日志块内的 RTF 标记),【决策:2026-09-24】**名字与正文都允许玩家 RTF**:
		/// - 名字**自带标签**时不套显示层配色(两套标记会互相干扰),只补 `&lt;/color&gt;` 收口;
		/// - 普通名字照旧用固定蓝色包裹;
		/// - 行尾再补一个 `&lt;/color&gt;`,让未闭合的颜色标签只影响自己这一行(其余标签如 `&lt;size&gt;`/`&lt;b&gt;` 无收口手段,
		///   属"允许 RTF"决策的自然后果)。
		/// 系统消息走固定灰斜体(文本来自 Locale,不含玩家输入)。</summary>
		private static string FormatLine(ChatMessage m)
		{
			if (m.IsSystem) return "<color=#9aa0a6><i>" + m.Text + "</i></color>";
			string name = NameOf(m);
			string namePart = name.IndexOf('<') >= 0
				? name + "</color>"
				: "<color=#8fb3ff>" + name + "</color>";
			return namePart + ": " + m.Text + "</color>";
		}

		private static string FormatPlain(ChatMessage m)
		{
			return m.IsSystem ? m.Text : NameOf(m) + ": " + m.Text;
		}

		private static string NameOf(ChatMessage m)
		{
			// 名字与正文同等对待:**允许 RTF**(【决策:2026-09-24】)。名字来自 Hello,内容不可信但只是显示层样式,
			// 无脚本面;自带标签时的标记冲突由 FormatLine 的"不套配色"分支规避。
			return string.IsNullOrEmpty(m.PlayerName) ? ("Player " + m.PlayerId) : m.PlayerName;
		}

		private static string ClampForPreview(string s)
		{
			if (string.IsNullOrEmpty(s)) return s;
			return s.Length <= PreviewMaxChars ? s : s.Substring(0, PreviewMaxChars) + "…";
		}

		// ---------------- 发送 ----------------

		private void OnSendClicked()
		{
			if (_chat == null) return;
			string text = _input != null ? _input.text : string.Empty;
			if (_input != null) _input.text = string.Empty;
			_chat.Send(text); // 内部 Trim + 空白丢弃 + 500 字截断;房主本地回显,客户端等房主中继回显
			RebuildLog();
			RefocusInput();
		}

		private void OnInputSubmitted(string value) { OnSendClicked(); }

		/// <summary>提交后保持焦点(SP2 连续输入形态):TMP 需要一帧才拿到焦点(尤其窗口显示动画期间)。</summary>
		private void RefocusInput()
		{
			if (_input == null) return;
			_input.ActivateInputField();
			if (isActiveAndEnabled) StartCoroutine(RefocusNextFrame());
		}

		private IEnumerator RefocusNextFrame()
		{
			yield return null;
			if (_input != null && _expanded) _input.ActivateInputField();
		}
	}
}
