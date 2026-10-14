"""Persistent local conversion worker. JSON lines in, JSON lines out."""
import json
import sys
from pathlib import Path

from hdr_gainmap import convert_jxr_to_gainmap


def serve():
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8", line_buffering=True)
    sys.stderr.reconfigure(encoding="utf-8")
    for line in sys.stdin:
        request_id = None
        try:
            request = json.loads(line)
            request_id = request.get("id")
            if request.get("op") == "shutdown":
                return
            if request.get("op") != "convert":
                raise ValueError("不支持的操作。")
            quality = request.get("quality", 95)
            if type(quality) is not int or not 1 <= quality <= 100:
                raise ValueError("输出质量必须是 1 到 100 之间的整数。")
            source = Path(request["source"])
            destination = Path(request["destination"])
            if source.suffix.lower() != ".jxr":
                raise ValueError("请选择 JXR 图片。")
            if not source.is_file():
                raise ValueError("原始图片不存在。")
            if not destination.parent.is_dir():
                raise ValueError("输出目录不存在。")
            if destination.exists() and request.get("overwrite") is not True:
                raise ValueError("同名 JPEG 已存在，请先确认覆盖。")
            info = convert_jxr_to_gainmap(source, destination, quality=quality)
            info["quality"] = quality
            reply = {"id": request_id, "success": True, "error": "", "info": info}
        except Exception as exc:
            reply = {"id": request_id, "success": False, "error": str(exc), "info": {}}
        print(json.dumps(reply, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    serve()
