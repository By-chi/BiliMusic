class_name BilibiliVideoApi
extends RefCounted

# 视频模块：视频信息、字幕、音频播放地址。
# 所有请求经由 BilibiliHttpClient（_http）发出。

var _http: BilibiliHttpClient
var _subtitle: BilibiliSubtitleManager
var _video_info_cache := {}

func _init(http: BilibiliHttpClient, subtitle: BilibiliSubtitleManager) -> void:
	_http = http
	_subtitle = subtitle

# ---------------- 视频信息 ----------------

func fetch_info(bvid: String, callback: Callable) -> void:
	_http.request("https://api.bilibili.com/x/web-interface/view?bvid=" + bvid, _on_video_info_response, [bvid, callback])

func _on_video_info_response(_r, code, _h, body, extra):
	var bvid: String = extra[0]
	var callback: Callable = extra[1]
	if code != 200:
		push_error("[BilibiliAPI] 获取视频信息失败 (%s): %d" % [bvid, code])
		callback.call({}); return
	var json = JSON.new()
	if json.parse(body.get_string_from_utf8()) != OK:
		push_error("[BilibiliAPI] JSON解析失败 (%s)" % bvid); callback.call({}); return
	var data = json.get_data()
	if data.get("code") != 0:
		push_error("[BilibiliAPI] API错误 (%s): %s" % [bvid, data.get("message")]); callback.call({}); return
	var vd = data.get("data", {})
	if vd.is_empty(): callback.call({}); return
	var own = vd.get("owner", {})
	var stat = vd.get("stat", {})
	var dim = vd.get("dimension", {})
	var pages: Array = vd.get("pages", [])
	var pages_info = []
	for p in pages:
		pages_info.append({
			"cid": p.get("cid", 0), "page": p.get("page", 1), "part": p.get("part", ""),
			"duration": p.get("duration", 0), "dimension": p.get("dimension", {}),
			"first_frame": p.get("first_frame", ""), "vid": p.get("vid", ""), "weblink": p.get("weblink", "")
		})
	var info = {
		"link": vd.get("bvid", bvid), "BV": vd.get("bvid", bvid), "aid": vd.get("aid", 0),
		"title": BilibiliHTMLDecoder.decode(vd.get("title", "")),
		"desc": BilibiliHTMLDecoder.decode(vd.get("desc", "")), "desc_v2": vd.get("desc_v2", []),
		"author": BilibiliHTMLDecoder.decode(own.get("name", "")), "mid": own.get("mid", 0),
		"face": own.get("face", ""), "pic": vd.get("pic", ""),
		"pubdate": vd.get("pubdate", 0), "ctime": vd.get("ctime", 0), "duration": vd.get("duration", 0),
		"cid": vd.get("cid", 0), "videos": vd.get("videos", 1), "copyright": vd.get("copyright", 1),
		"tid": vd.get("tid", 0), "tname": vd.get("tname", ""), "tid_v2": vd.get("tid_v2", 0),
		"tname_v2": vd.get("tname_v2", ""), "dynamic": vd.get("dynamic", ""),
		"dimension": {"width": dim.get("width", 0), "height": dim.get("height", 0), "rotate": dim.get("rotate", 0)},
		"rights": vd.get("rights", {}),
		"stat": {"view": stat.get("view", 0), "danmaku": stat.get("danmaku", 0), "like": stat.get("like", 0),
			"coin": stat.get("coin", 0), "favorite": stat.get("favorite", 0), "share": stat.get("share", 0),
			"reply": stat.get("reply", 0), "now_rank": stat.get("now_rank", 0), "his_rank": stat.get("his_rank", 0),
			"dislike": stat.get("dislike", 0), "evaluation": stat.get("evaluation", "")},
		"subtitle": vd.get("subtitle", {}), "pages": pages_info, "season_id": vd.get("season_id", 0)
	}
	callback.call(info)

# ---------------- 字幕 ----------------

func fetch_subtitle_auto(bvid: String, callback: Callable, save_path: String = "") -> void:
	fetch_info(bvid, func(info: Dictionary):
		if info.is_empty():
			callback.call({})
			return
		_video_info_cache[bvid] = info
		_subtitle.fetch_subtitle_auto(bvid, info, callback, save_path)
	)

func fetch_subtitle_with_info(info: Dictionary, callback: Callable, save_path: String = "") -> void:
	var bvid = info.get("link", "")
	if bvid.is_empty() or info.get("cid", 0) == 0:
		callback.call({})
		return
	_video_info_cache[bvid] = info
	_subtitle.fetch_subtitle_auto(bvid, info, callback, save_path)

# ==================== 音频播放地址 ====================
# 说明：
#   未登录时 C# 侧 M4SAudioPlayer.PlayByIdentifierAsync 会直接调用
#   DownloadAudio.GetAudioInfoByBvSync（.NET HttpClient）拿音频 URL，
#   不会走到这里。
#   只有已登录时，C# 才会通过 fetch_audio_url 回调到本函数，
#   因此本函数只需处理"已登录 → WBI 签名"这一条主路径。

func _is_logged_in() -> bool:
	var sess = GdScriptFunc.get_data("AccountData", "SESSDATA", "")
	if sess == null:
		return false
	if sess is String and sess.strip_edges().is_empty():
		return false
	return true


func fetch_audio_url(bvid: String, cid: int, callback: Callable) -> void:
	if cid <= 0:
		callback.call("")
		return

	if _is_logged_in():
		_try_fetch(bvid, cid, "WBI", "PLAIN", callback)
	else:
		# 理论不会走到，保底
		_try_fetch(bvid, cid, "PLAIN", "WBI", callback)


func _try_fetch(bvid: String, cid: int, mode: String, fallback_mode: String, callback: Callable) -> void:
	_do_fetch(bvid, cid, mode, func(url: String):
		if not url.is_empty():
			callback.call(url)
			return
		print("[BilibiliVideoApi] %s 失败，回退到 %s" % [mode, fallback_mode])
		_do_fetch(bvid, cid, fallback_mode, func(url2: String):
			if url2.is_empty():
				push_error("[BilibiliVideoApi] 两条路径均失败，无法获取音频地址")
			callback.call(url2)
		)
	)


func _do_fetch(bvid: String, cid: int, mode: String, callback: Callable) -> void:
	var url: String
	var headers: PackedStringArray
	var use_wbi := false

	if mode == "WBI":
		url = "https://api.bilibili.com/x/player/wbi/playurl?fnval=80&qn=80&fourk=0&otype=json&bvid=%s&cid=%d" % [bvid, cid]
		headers = _http.with_origin(
			_http.get_headers(),
			"https://www.bilibili.com/video/" + bvid,
			"https://www.bilibili.com"
		)
		use_wbi = true
	elif mode == "PLAIN":
		url = "https://api.bilibili.com/x/player/playurl?fnval=80&qn=80&fourk=0&otype=json&bvid=%s&cid=%d" % [bvid, cid]
		# 未登录的 PLAIN 路径：只有 buvid3 + b_nut 的轻量 Cookie，其余走统一 UA/Referer
		var buvid3 := BilibiliCookieStore.get_or_generate_buvid()
		var b_nut := str(int(Time.get_unix_time_from_system()))
		headers = _http.with_origin(
			_http.get_headers(),
			"https://www.bilibili.com/video/" + bvid,
			"https://www.bilibili.com"
		)
		# 将完整 Cookie 替换为轻量 Cookie（未登录不携带会话信息）
		for i in headers.size():
			if headers[i].begins_with("Cookie: "):
				headers[i] = "Cookie: buvid3=" + buvid3 + "; b_nut=" + b_nut + ";"
	else:
		push_error("[BilibiliVideoApi] 未知模式: " + mode)
		callback.call("")
		return

	var extra := {"tag": mode, "callback": callback}
	if use_wbi:
		_http.request_with_sign(url, _on_audio_url_response, extra, HTTPClient.METHOD_GET, headers)
	else:
		_http.request(url, _on_audio_url_response, extra, HTTPClient.METHOD_GET, headers)


func _on_audio_url_response(_result: int, code: int, _headers: PackedStringArray, body: PackedByteArray, extra: Variant) -> void:
	var tag := "?"
	var callback: Callable = Callable()
	if extra is Dictionary:
		tag = extra.get("tag", "?")
		var cb = extra.get("callback")
		if cb is Callable:
			callback = cb
	if not callback.is_valid():
		return

	if code != 200:
		push_error("[BilibiliVideoApi] [%s] 获取音频地址失败 HTTP %d" % [tag, code])
		callback.call("")
		return

	var json = JSON.new()
	if json.parse(body.get_string_from_utf8()) != OK:
		push_error("[BilibiliVideoApi] [%s] JSON 解析失败" % tag)
		callback.call("")
		return

	var data = json.get_data()
	if typeof(data) != TYPE_DICTIONARY or data.get("code", -1) != 0:
		push_error("[BilibiliVideoApi] [%s] API 错误: %s" % [tag, data.get("message", "")])
		callback.call("")
		return

	var d: Dictionary = data.get("data", {})
	if d.is_empty():
		callback.call("")
		return

	var dash: Dictionary = d.get("dash", {})
	var audios: Array = dash.get("audio", [])
	if audios.is_empty():
		var durl: Array = d.get("durl", [])
		if not durl.is_empty():
			callback.call(durl[0].get("url", ""))
		else:
			callback.call("")
		return

	var best: Dictionary = audios[0]
	for a in audios:
		if int(a.get("bandwidth", 0)) > int(best.get("bandwidth", 0)):
			best = a
	var base_url: String = best.get("baseUrl", best.get("base_url", ""))
	callback.call(base_url)
