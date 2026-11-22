"""Exercise actual keyboard handling without importing receiver hardware libraries."""
import ast
import contextlib
import io
from pathlib import Path
from types import SimpleNamespace

source = Path(__file__).resolve().parents[1] / "receiver.py"
tree = ast.parse(source.read_text(encoding="utf-8"))
function = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "handle_key")
namespace = {"SharedPreview": object}
exec(compile(ast.Module(body=[function], type_ignores=[]), str(source), "exec"), namespace)
for key in ("q", "Q", "\x1b"):
    shared = SimpleNamespace(running=True)
    output = io.StringIO()
    with contextlib.redirect_stdout(output):
        namespace["handle_key"](shared, key)
        namespace["handle_key"](shared, key)
    assert not shared.running
    assert output.getvalue().splitlines() == ["QPRO_TRACKING_USER_QUIT source=keyboard"]
events = []
shared = SimpleNamespace(running=True, select=lambda x: events.append(x),
                         queue_runtime_key=lambda x: events.append(x),
                         queue_calibration_key=lambda x: events.append(x))
with contextlib.redirect_stdout(io.StringIO()) as output:
    for key in ("0", "4", "s", "t", "r"):
        namespace["handle_key"](shared, key)
assert shared.running and events == [0, 4, "strip", "t", "r"] and not output.getvalue()
print("PASS receiver explicit Q/Escape marker, duplicate quit and unchanged other keys; no audio/hardware")
