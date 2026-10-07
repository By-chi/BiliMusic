class_name BilibiliSearchApi
extends RefCounted

# 搜索模块：视频搜索、B站音乐周榜。
# 所有请求经由 BilibiliHttpClient（_http）发出。

var _http: BilibiliHttpClient

func _init(http: BilibiliHttpClient) -> void:
	_http = http

func search(callback: Callable, keyword: String, num: int = 10, order = 0, page := 1, author: String = "", _tids := 3) -> void:
	if keyword == "bilibili音乐周榜":
		_fetch_music_rank_static(callback)
		return
	var order_str = BilibiliConstants.ORDER_MAP.get(order, "totalrank") if order is int else order
	var query = {"keyword": keyword, "page": page, "order": order_str, "page_size": num, "search_type": "video"}
	var qs = ""
	for k in query:
		if not qs.is_empty(): qs += "&"
		qs += k + "=" + str(query[k]).uri_encode()
	var url = "https://api.bilibili.com/x/web-interface/search/type?" + qs
	var headers = _http.with_origin(_http.get_headers(), "https://www.bilibili.com", "https://www.bilibili.com")
	_http.request(url, _on_search_response, [callback, author])

func _on_search_response(_r, code, _h, body, extra):
	var callback: Callable = extra[0]
	var author_filter: String = extra[1] if extra.size() > 1 else ""
	if code != 200:
		push_error("[BilibiliAPI] 搜索请求失败: %d" % code)
		callback.call([{}]); return
	var raw = body.get_string_from_utf8()
	if raw.strip_edges().begins_with("<"):
		push_error("[BilibiliAPI] 搜索被风控拦截，收到HTML"); callback.call([{}]); return
	var json = JSON.new()
	if json.parse(raw) != OK:
		push_error("[BilibiliAPI] JSON解析失败"); callback.call([{}]); return
	var data = json.get_data()
	if data.get("code") != 0:
		push_error("[BilibiliAPI] API错误: %s" % data.get("message")); callback.call([{}]); return
	var videos = []
	for item in data.get("data", {}).get("result", []):
		var bvid = item.get("bvid", "")
		if bvid.is_empty(): continue
		if author_filter != "" and item.get("author", "") != author_filter: continue
		videos.append({
			"link": bvid, "BV": bvid,
			"title": BilibiliHTMLDecoder.decode(item.get("title", "").replace('<em class="keyword">', "").replace("</em>", "")),
			"author": BilibiliHTMLDecoder.decode(item.get("author", "")),
			"play": item.get("play", 0),
			"danmaku": item.get("video_review", 0),
			"duration": item.get("duration", ""),
			"description": BilibiliHTMLDecoder.decode(item.get("description", ""))
		})
	callback.call(videos)

# ---------------- 音乐周榜 ----------------

func _fetch_music_rank_static(callback: Callable) -> void:
	_http.request("https://api.bilibili.com/x/copyright-music-publicity/toplist/all_period?list_type=1", _on_all_period_response, [callback])

func _on_all_period_response(result, code, _h, body, extra):
	var callback: Callable = extra[0]
	if result != HTTPRequest.RESULT_SUCCESS or code != 200:
		push_error("[BilibiliAPI] 获取榜单ID失败"); callback.call([{}]); return
	var json = JSON.new()
	if json.parse(body.get_string_from_utf8()) != OK: callback.call([{}]); return
	var data = json.get_data()
	if data.get("code", -1) != 0: callback.call([{}]); return
	var periods = data.get("data", {}).get("list", {})
	var latest_id = 0; var latest_time = 0
	for year in periods:
		for period in periods[year]:
			if period.get("publish_time", 0) > latest_time:
				latest_time = period.get("publish_time", 0)
				latest_id = period.get("ID", 0)
	if latest_id == 0: callback.call([{}]); return
	_fetch_music_list_static(latest_id, callback)

func _fetch_music_list_static(list_id: int, callback: Callable) -> void:
	_http.request("https://api.bilibili.com/x/copyright-music-publicity/toplist/music_list?list_id=%d" % list_id, _on_music_list_response, [callback])

func _on_music_list_response(_r, code, _h, body, extra):
	var callback: Callable = extra[0]
	if _r != HTTPRequest.RESULT_SUCCESS or code != 200: callback.call([{}]); return
	var json = JSON.new()
	if json.parse(body.get_string_from_utf8()) != OK: callback.call([{}]); return
	var data = json.get_data()
	if data.get("code", -1) != 0: callback.call([{}]); return
	var list = data.get("data", {}).get("list", [])
	var videos = []
	for item in list:
		var bvid = item.get("creation_bvid", "")
		if bvid.is_empty(): bvid = item.get("mv_bvid", "")
		if bvid.is_empty(): continue
		videos.append({
			"link": bvid, "BV": bvid,
			"title": BilibiliHTMLDecoder.decode(item.get("creation_title", "")),
			"author": BilibiliHTMLDecoder.decode(item.get("creation_nickname", "")),
			"description": BilibiliHTMLDecoder.decode(item.get("creation_reason", "")),
			"play": item.get("creation_play", 0)
		})
	callback.call(videos)
