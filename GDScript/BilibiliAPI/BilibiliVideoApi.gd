class_name BilibiliVideoApi
extends RefCounted

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
