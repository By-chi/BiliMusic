class_name BilibiliSubtitleManager

var _api_request_func: Callable
var _download_request_func: Callable
var _subtitle_correction: Node
var _m4s_audio_player: Node
var _pending := {}

func _init(api_request_func: Callable, download_request_func: Callable, subtitle_correction: Node, m4s_audio_player: Node) -> void:
	_api_request_func = api_request_func
	_download_request_func = download_request_func
	_subtitle_correction = subtitle_correction
	_m4s_audio_player = m4s_audio_player

func fetch_subtitle_auto(bvid: String, info: Dictionary, callback: Callable, save_path: String = "") -> void:
	var cid = info.get("cid", 0)
	if cid == 0:
		push_error("无有效 cid")
		callback.call({})
		return
	_fetch_subtitle_list(bvid, cid, info, callback, save_path)

func _fetch_subtitle_list(bvid: String, cid: int, info: Dictionary, callback: Callable, save_path: String) -> void:
	var url = "https://api.bilibili.com/x/player/wbi/v2?bvid=%s&cid=%d" % [bvid, cid]
	_api_request_func.call(url, _on_list_received, [bvid, info, callback, save_path])

func _on_list_received(result: int, response_code: int, _headers: PackedStringArray, body: PackedByteArray, extra: Variant) -> void:
	var arr: Array = extra
	var bvid: String = arr[0]
	var info: Dictionary = arr[1]
	var callback: Callable = arr[2]
	var save_path: String = arr[3]

	if result != HTTPRequest.RESULT_SUCCESS or response_code != 200:
		push_error("字幕列表获取失败 (%s)" % bvid)
		callback.call({})
		return

	var json = JSON.new()
	if json.parse(body.get_string_from_utf8()) != OK:
		push_error("JSON解析失败 (%s)" % bvid)
		callback.call({})
		return
	var root = json.get_data()
	if root.get("code") != 0:
		push_error("API错误 (%s): %s" % [bvid, root.get("message")])
		callback.call({})
		return

	var data = root.get("data", {})
	var si = data.get("subtitle", {})
	var subtitles: Array = si.get("subtitles", [])
	if subtitles.is_empty():
		var ai = si.get("ai_subtitle", {})
		subtitles = ai.get("subtitles", [])

	if subtitles.is_empty():
		push_warning("无可用字幕 (%s)" % bvid)
		_fallback_external(info, callback, save_path)
		return

	var video_lang: String = si.get("video_lang", "zh-CN")
	var non_ai = []
	var ai_list = []
	for sub in subtitles:
		var lan: String = sub.get("lan", "")
		var u: String = sub.get("subtitle_url", "")
		if u.is_empty():
			u = sub.get("subtitle_url_v2", "")
		if u.is_empty():
			continue
		if u.begins_with("//"):
			u = "https:" + u
		if not lan.begins_with("ai-"):
			non_ai.append({"url": u, "is_ai": false, "lan": lan})
		else:
			ai_list.append({"url": u, "is_ai": true, "lan": lan})

	var sort_by_lang = func(a, b):
		var ap = 2 if a.lan == video_lang else (1 if a.lan == "zh-CN" else 0)
		var bp = 2 if b.lan == video_lang else (1 if b.lan == "zh-CN" else 0)
		return ap > bp

	if not non_ai.is_empty():
		non_ai.sort_custom(sort_by_lang)
		_try_download(0, non_ai, bvid, info, callback, false, save_path)
		return
	if not ai_list.is_empty():
		ai_list.sort_custom(sort_by_lang)
		_try_download(0, ai_list, bvid, info, callback, false, save_path)
		return
	_fallback_external(info, callback, save_path)

func _try_download(index: int, candidates: Array, bvid: String, info: Dictionary, callback: Callable, skip_correction: bool, save_path: String) -> void:
	if index >= candidates.size():
		push_warning("所有候选字幕失败 (%s)，尝试外部字幕" % bvid)
		_fallback_external(info, callback, save_path)
		return

	var c = candidates[index]
	var url: String = c.url
	var is_ai: bool = c.is_ai

	var headers = [
		"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
		"Referer: https://www.bilibili.com/video/" + bvid
	]

	_download_request_func.call(url, func(result, code, _h, body, ext):
		if code != 200:
			push_warning("字幕下载失败 (%s) HTTP %d" % [bvid, code])
			_try_download(index + 1, candidates, bvid, info, callback, skip_correction, save_path)
			return

		var json = JSON.new()
		if json.parse(body.get_string_from_utf8()) != OK:
			push_warning("字幕JSON解析失败 (%s)" % bvid)
			_try_download(index + 1, candidates, bvid, info, callback, skip_correction, save_path)
			return

		var content = json.get_data()
		if typeof(content) != TYPE_DICTIONARY:
			push_warning("字幕结构异常 (%s)" % bvid)
			_try_download(index + 1, candidates, bvid, info, callback, skip_correction, save_path)
			return

		# 纯音乐占比过高则跳过当前候选，继续尝试下一个
		if _check_music_ratio(content):
			push_warning("纯音乐占比过高 (%s)" % bvid)
			_try_download(index + 1, candidates, bvid, info, callback, skip_correction, save_path)
			return

		var final_skip = true
		if is_ai:
			final_skip = skip_correction or not GdScriptFunc.get_data("Options", "SubtitleTextCorrection", false)

		if final_skip:
			_generate_lrc(content, bvid, callback, save_path)
		else:
			_perform_correction(content, bvid, info, callback, save_path)

	, [bvid, info, callback, save_path, candidates, index, skip_correction], HTTPClient.METHOD_GET, headers)
func _check_music_ratio(sub: Dictionary) -> bool:
	var body = sub.get("body", [])
	if not body is Array or body.is_empty():
		return false
	var count = 0
	for e in body:
		if typeof(e) != TYPE_DICTIONARY:
			continue
		var t: String = e.get("content", "").replace("♪", "").strip_edges()
		if t == "音乐" or t.to_lower() == "music":
			count += 1
	return float(count) / body.size() > 0.4

func _generate_lrc(content: Dictionary, bvid: String, callback: Callable, save_path: String) -> void:
	var body = content.get("body", [])
	if body.is_empty():
		callback.call({})
		return
	var lrc_text = ""
	for e in body:
		var from_sec: float = e.get("from", 0.0)
		var text: String = e.get("content", "").replace("♪", "").strip_edges()
		if text.is_empty():
			continue
		var m = int(from_sec / 60)
		var s = int(from_sec) % 60
		var ms = int(round((from_sec - int(from_sec)) * 100))
		lrc_text += "[%02d:%02d.%02d]%s\n" % [m, s, ms, text]

	var lrc_path: String
	if not save_path.is_empty():
		lrc_path = save_path
		DirAccess.make_dir_recursive_absolute(lrc_path.get_base_dir())
	else:
		var dir = BilibiliConstants.LYRICS_CACHE_DIR
		if not DirAccess.dir_exists_absolute(dir):
			DirAccess.make_dir_recursive_absolute(dir)
		var fname = "bili_%s_%d.lrc" % [bvid, Time.get_unix_time_from_system()]
		lrc_path = dir.path_join(fname)
	var file = FileAccess.open(lrc_path, FileAccess.WRITE)
	if file:
		file.store_string(lrc_text)
		file.close()
		callback.call({"type": "aligned_lrc", "path": lrc_path})
	else:
		push_error("无法写入LRC: " + lrc_path)
		callback.call({})

func _perform_correction(content: Dictionary, bvid: String, info: Dictionary, callback: Callable, save_path: String) -> void:
	if not is_instance_valid(_subtitle_correction):
		push_error("SubtitleCorrection 不可用")
		_generate_lrc(content, bvid, callback, save_path)
		return
	var audio = _get_current_audio_path()
	if audio.is_empty():
		_generate_lrc(content, bvid, callback, save_path)
		return
	var track_name: String = CSharpFunc.ExtractSongName(info.get("title", ""))
	var rid = str(Time.get_ticks_msec()) + "_" + str(randi())
	_pending[rid] = {"callback": callback, "fallback": content, "save_path": save_path, "info": info}
	_subtitle_correction.ProcessSubtitleAsync(content, audio, track_name, BilibiliConstants.LYRICS_CACHE_DIR, rid)

func _fallback_external(info: Dictionary, callback: Callable, save_path: String) -> void:
	if not is_instance_valid(_subtitle_correction):
		callback.call({})
		return
	var audio = _get_current_audio_path()
	if audio.is_empty():
		callback.call({})
		return
	var track_name: String = CSharpFunc.ExtractSongName(info.get("title", ""))
	var rid = str(Time.get_ticks_msec()) + "_" + str(randi())
	_pending[rid] = {"callback": callback, "fallback": {}, "save_path": save_path, "info": info}
	_subtitle_correction.FetchAndAlignExternalAsync(audio, track_name, BilibiliConstants.LYRICS_CACHE_DIR, rid)

func handle_correction_result(request_id: String, lrc_path: String) -> void:
	if not _pending.has(request_id):
		return
	var ctx = _pending[request_id]
	_pending.erase(request_id)
	var callback: Callable = ctx.callback
	var fallback = ctx.fallback
	var save_path: String = ctx.save_path

	if lrc_path.is_empty() or not FileAccess.file_exists(lrc_path):
		if typeof(fallback) == TYPE_DICTIONARY and fallback.has("body"):
			_generate_lrc(fallback, "", callback, save_path)
		else:
			callback.call({})
		return
	callback.call({"type": "aligned_lrc", "path": lrc_path})

func _get_current_audio_path() -> String:
	if not is_instance_valid(_m4s_audio_player):
		return ""
	return _m4s_audio_player.CurrentAudioFilePath
