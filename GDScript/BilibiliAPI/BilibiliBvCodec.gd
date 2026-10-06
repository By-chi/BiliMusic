class_name BilibiliBvCodec
extends RefCounted

const XOR_CODE: int = 23442827791579
const MASK_CODE: int = 2251799813685247
const MAX_AID: int = 1 << 51
const BASE: int = 58
const BV_CHARS: String = "FcwAPNKTMug3GV5Lj7EJnHpWsx4tb8haYeviqBz6rkCy12mUSDQX9RdoZf"

static func bv_to_aid(bvid: String) -> int:
	if not bvid.begins_with("BV1") or bvid.length() != 12:
		return 0
	var part = bvid.substr(3)
	var aid: int = 0
	for i in part.length():
		var idx = BV_CHARS.find(part[i])
		if idx == -1:
			return 0
		aid = aid * BASE + idx
	aid = (aid & MASK_CODE) ^ XOR_CODE
	return aid if aid < MAX_AID else 0

static func format_duration(seconds) -> String:
	if seconds is String:
		return seconds
	var s = int(seconds)
	var m = s / 60
	var sec = s % 60
	return "%02d:%02d" % [m, sec]
