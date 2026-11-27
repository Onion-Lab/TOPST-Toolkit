# -*- coding: utf-8 -*-
"""Model zoo (design section 5.9): exports one model of engine/zoo/zoo.json to ONNX, the way the review checked it.

    python zoo_export.py --toolkit <root> --out <json> --site <components>/zoo/site [--config <folder>]
        --lib torchvision|timm|ultralytics --name <model> --task class|obj --size <pixels>
        [--plain] [--relu6] [--weights <file> [--weights-tag <tag>]] --onnx <file.onnx>

Runs with the engine's Python, the zoo component's packages first on sys.path (timm, Ultralytics, onnx 1.17,
onnxslim: their opencv and onnx must not meet the toolkit's, so never in a process that imports enlight).
Without --weights the model gets random weights, which is what the review exported (structure only).

The recipe of each model is in the catalog. --plain: the export of the review's first round (2026-11-22), as is. Without
it, what the second round taught (same numbers, in a form the NPU runs; each one is reported in "fixes"): one plain run
before tracing and TF-"SAME" padding worked out as numbers, timm's re-parameterisable models in their deploy form,
mean([2, 3]) as GlobalAveragePool, ShuffleNet's channel shuffle as a 1 x 1 convolution, and with --relu6 hard-sigmoid as
clip(x + 3, 0, 6) / 6. build/zoo/zoo_probe.py exports the review's candidates with these same functions.
"""
import os
import shutil
import sys
import time

# the recipe of the export at hand (zoo_probe.py and main() set them)
HARD_SIGMOID_AS_RELU6 = False   # --relu6: see hard_sigmoid_to_relu6
PLAIN = False                   # --plain: the export of the first round (2026-11-22), none of the fixes


def add_site(site, config=None):
    """The zoo component's packages first on sys.path. Ultralytics: never install anything by itself, never phone home,
    and keep its settings file out of the user's profile."""
    if site and site not in sys.path:
        sys.path.insert(0, site)
    os.environ.setdefault("YOLO_AUTOINSTALL", "False")
    os.environ.setdefault("YOLO_OFFLINE", "True")
    if config:
        os.makedirs(config, exist_ok=True)
        os.environ.setdefault("YOLO_CONFIG_DIR", config)


def params_of(model):
    return int(sum(p.numel() for p in model.parameters()))


def slim(path):
    """Folds what the engine's PyTorch 1.12 leaves symbolic (output shapes computed by Shape/Gather ops), as Ultralytics
    does for its own exports with onnxslim; the toolkit's converter cannot read a model whose output sizes are not numbers."""
    import onnx
    import onnxslim
    model = onnxslim.slim(path)
    if model is not None:
        onnx.save(model, path)


def mean_to_pool(path):
    """`x.mean([2, 3])` (torchvision MNASNet, ShuffleNet) exports as ReduceMean, which the NPU does not run; the same
    average over the whole picture as GlobalAveragePool (+ Flatten when the 1 x 1 is dropped) is what it runs.
    Returns how many were replaced."""
    import onnx
    from onnx import helper
    model = onnx.load(path)
    graph = model.graph
    nodes, changed = [], 0
    for n in graph.node:
        axes = [a.ints for a in n.attribute if a.name == "axes"]
        keep = [a.i for a in n.attribute if a.name == "keepdims"]
        if n.op_type == "ReduceMean" and axes and sorted(axes[0]) in ([2, 3], [-2, -1]):
            if keep and keep[0] == 0:
                pooled = n.output[0] + "_pooled"
                nodes.append(helper.make_node("GlobalAveragePool", [n.input[0]], [pooled], name=n.name + "_pool"))
                nodes.append(helper.make_node("Flatten", [pooled], [n.output[0]], name=n.name + "_flatten", axis=1))
            else:
                nodes.append(helper.make_node("GlobalAveragePool", [n.input[0]], [n.output[0]], name=n.name + "_pool"))
            changed += 1
        else:
            nodes.append(n)
    if changed:
        del graph.node[:]
        graph.node.extend(nodes)
        onnx.save(model, path)
    return changed


def shuffle_to_conv(path):
    """ShuffleNet's channel shuffle (Reshape to N x g x C/g x H x W, Transpose 0 2 1 3 4, Reshape back) is a 5-D
    Permute the NPU does not run. It only moves channels, which a 1 x 1 convolution with 0/1 weights does exactly.
    Returns how many were replaced."""
    import numpy as np
    import onnx
    from onnx import helper, numpy_helper, shape_inference
    model = onnx.load(path)
    graph = model.graph
    inferred = shape_inference.infer_shapes(model).graph
    shapes = {}
    for v in list(inferred.value_info) + list(inferred.input) + list(inferred.output):
        dims = [d.dim_value for d in v.type.tensor_type.shape.dim]
        if dims and all(d > 0 for d in dims):
            shapes[v.name] = dims
    producer = {o: n for n in graph.node for o in n.output}
    readers = {}
    for n in graph.node:
        for i in n.input:
            readers.setdefault(i, []).append(n)
    drop, add = set(), {}
    for t in graph.node:
        perm = [a.ints for a in t.attribute if a.name == "perm"]
        if t.op_type != "Transpose" or not perm or list(perm[0]) != [0, 2, 1, 3, 4]:
            continue
        first = producer.get(t.input[0])
        after = readers.get(t.output[0], [])
        if first is None or first.op_type != "Reshape" or len(after) != 1 or after[0].op_type != "Reshape":
            continue
        five, x = shapes.get(t.input[0]), first.input[0]
        if five is None or len(five) != 5 or x not in shapes or len(readers.get(first.output[0], [])) != 1:
            continue
        g, per = five[1], five[2]
        c = g * per
        weight = np.zeros((c, c, 1, 1), dtype=np.float32)
        for j in range(per):
            for k in range(g):
                weight[j * g + k, k * per + j, 0, 0] = 1.0
        name = t.name + "_shuffle"
        graph.initializer.append(numpy_helper.from_array(weight, name + "_w"))
        # every attribute spelled out: the converter multiplies with them and has no defaults
        add[id(first)] = helper.make_node("Conv", [x, name + "_w"], [after[0].output[0]], name=name, kernel_shape=[1, 1],
                                          strides=[1, 1], pads=[0, 0, 0, 0], dilations=[1, 1], group=1)
        drop.update([id(t), id(after[0])])
    if not add:
        return 0
    nodes = []
    for n in graph.node:
        if id(n) in add:
            nodes.append(add[id(n)])
        elif id(n) not in drop:
            nodes.append(n)
    del graph.node[:]
    graph.node.extend(nodes)
    onnx.save(model, path)
    return len(add)


def hard_sigmoid_to_relu6(path):
    """Hard-sigmoid (the gate of squeeze-excitation in MobileNetV3, LCNet, GhostNet ...; inside hard-swish too) is an
    activation the compiler cannot put behind a convolution ("LReLU, ReLU, Bypass only"). The same numbers as a
    ReLU6 the NPU knows from MobileNetV2: hardsigmoid(x) = clip(x + beta/alpha, 0, 1/alpha) * alpha.
    Returns how many were replaced."""
    import numpy as np
    import onnx
    from onnx import helper, numpy_helper
    model = onnx.load(path)
    graph = model.graph
    nodes, changed = [], 0
    for n in graph.node:
        if n.op_type != "HardSigmoid":
            nodes.append(n)
            continue
        attrs = {a.name: a.f for a in n.attribute}
        alpha, beta = attrs.get("alpha", 0.2), attrs.get("beta", 0.5)
        base = (n.name or n.output[0]) + "_hs"
        consts = {base + "_shift": beta / alpha, base + "_low": 0.0, base + "_high": 1.0 / alpha, base + "_scale": alpha}
        for name, value in consts.items():
            graph.initializer.append(numpy_helper.from_array(np.array(value, dtype=np.float32), name))
        nodes.append(helper.make_node("Add", [n.input[0], base + "_shift"], [base + "_shifted"], name=base + "_add"))
        nodes.append(helper.make_node("Clip", [base + "_shifted", base + "_low", base + "_high"], [base + "_clipped"], name=base + "_clip"))
        nodes.append(helper.make_node("Mul", [base + "_clipped", base + "_scale"], [n.output[0]], name=base + "_mul"))
        changed += 1
    if changed:
        del graph.node[:]
        graph.node.extend(nodes)
        onnx.save(model, path)
    return changed


def export_torch(model, size, path, opset):
    import torch
    model.eval()
    x = torch.randn(1, 3, size, size)
    with torch.no_grad():
        if not PLAIN:
            # one plain run first: modules that work something out from the picture size on their first call and
            # keep it (timm's Conv2dSameExport) then put numbers into the file, not tracing arithmetic
            model(x)
        torch.onnx.export(model, x, path, opset_version=opset, input_names=["images"], output_names=["output"],
                          do_constant_folding=True)
    fixes = {}
    try:
        slim(path)
    except Exception as e:  # noqa: BLE001 - the unslimmed file is still worth checking
        fixes["slimError"] = (type(e).__name__ + ": " + str(e))[:200]
    if PLAIN:
        return fixes
    replaced = mean_to_pool(path)
    if replaced:
        fixes["meanToPool"] = replaced
    replaced = shuffle_to_conv(path)
    if replaced:
        fixes["shuffleToConv"] = replaced
    if HARD_SIGMOID_AS_RELU6:
        replaced = hard_sigmoid_to_relu6(path)
        if replaced:
            fixes["hardSigmoidToRelu6"] = replaced
    if any(k != "slimError" for k in fixes):
        try:
            slim(path)   # drops what the rewrites left unused (the shapes of the replaced Reshapes)
        except Exception:  # noqa: BLE001
            pass
    return fixes


def torchvision_weights(name, tag=None):
    """The torchvision 0.13 weights entry of a classification model: ResNet18_Weights.IMAGENET1K_V1 for "resnet18"."""
    import torchvision.models as tvm
    key = name.replace("_", "").lower()
    for attr in dir(tvm):
        if attr.endswith("_Weights") and attr[: -len("_Weights")].replace("_", "").lower() == key:
            enum = getattr(tvm, attr)
            return enum[tag] if tag else enum.DEFAULT
    raise KeyError("no torchvision weights for " + name)


def export_one(c, path, opset, weights=None, weights_tag=None):
    """Exports one model; returns (parameters, fixes made on the way). c: lib, name, task, size.
    weights: the file with the trained weights (torchvision .pth, timm .safetensors / .bin, Ultralytics .pt), or None."""
    lib, name, size = c["lib"], c["name"], c["size"]
    if lib == "torchvision":
        import torch
        import torchvision
        if c["task"] == "obj":
            model = getattr(torchvision.models.detection, name)(weights=None, weights_backbone=None)
            model.eval()
            images = [torch.rand(3, size, size)]
            with torch.no_grad():
                torch.onnx.export(model, (images,), path, opset_version=max(opset, 11), input_names=["images"])
            return params_of(model), {}
        if c["task"] == "seg":
            model = getattr(torchvision.models.segmentation, name)(weights=None, weights_backbone=None, aux_loss=False)

            class Out(torch.nn.Module):  # the "out" map only
                def __init__(self, m):
                    super().__init__()
                    self.m = m

                def forward(self, x):
                    return self.m(x)["out"]

            return params_of(model), export_torch(Out(model), size, path, opset)
        builder = getattr(torchvision.models, name)
        if weights is None:
            model = builder(weights=None)
        else:
            # torchvision's own loading (key renames of old checkpoints, the class count of the weights), from the file
            # the app downloaded and checked instead of its URL
            import torchvision.models._api as api
            entry = torchvision_weights(name, weights_tag)
            local = os.path.abspath(weights)
            api.load_state_dict_from_url = lambda url, *a, **k: torch.load(local, map_location="cpu")
            kwargs = {}
            if name in ("inception_v3", "googlenet"):
                # with weights torchvision would put a rescaling of each colour in front (transform_input): the
                # structure the review checked is without it, and the catalog asks for mean = std = 0.5 instead
                kwargs["transform_input"] = False
            model = builder(weights=entry, **kwargs)
        return params_of(model), export_torch(model, size, path, opset)
    if lib == "timm":
        import timm
        import timm.layers.padding as padding
        from timm.utils.model import reparameterize_model
        if not PLAIN and not getattr(padding, "zoo_static", False):
            # TF-"SAME" padding (tf_efficientnet*, tf_mobilenetv3*) is worked out from the picture size at run time;
            # traced, that becomes Shape · Ceil · Clip arithmetic the converter cannot read. The picture size is fixed
            # here, so the padding is worked out once, as numbers
            dynamic = padding.get_same_padding

            def get_same_padding(x, kernel_size, stride, dilation):
                return dynamic(int(x), kernel_size, stride, dilation)

            padding.get_same_padding = get_same_padding
            padding.zoo_static = True
        if weights is None:
            model = timm.create_model(name, pretrained=False, exportable=True)
        else:
            model = timm.create_model(name, pretrained=True, pretrained_cfg_overlay={"file": os.path.abspath(weights)}, exportable=True)
        model.eval()
        # the deploy form of the re-parameterisable families (RepVGG, MobileOne, FastViT, RepGhost, RepViT ...): the
        # training-time branches merged into one convolution, the same numbers in one layer, as they are meant to run
        deployed = model if PLAIN else reparameterize_model(model)
        params = params_of(deployed)
        fixes = export_torch(deployed, size, path, opset)
        if params != params_of(model):
            fixes["reparameterized"] = True
        return params, fixes
    if lib == "ultralytics":
        from ultralytics import YOLO
        if weights is None:
            yolo = YOLO(name + ".yaml")
        else:
            # Ultralytics writes the .onnx next to the .pt: export a copy in the working folder (the current one)
            local = os.path.abspath(os.path.basename(weights))
            if os.path.abspath(weights) != local:
                shutil.copyfile(weights, local)
            yolo = YOLO(local)
        params = params_of(yolo.model)
        made = yolo.export(format="onnx", imgsz=size, opset=opset, simplify=True, dynamic=False, half=False, verbose=False)
        made = str(made)
        if os.path.abspath(made) != os.path.abspath(path):
            os.replace(made, path)
        return params, {}
    raise ValueError("unknown library " + lib)


def needs_odd(c):
    """The TF ports in timm keep TF-"SAME" padding (see odd_size)."""
    return c["lib"] == "timm" and c["name"].startswith("tf_")


def odd_size(n):
    """The picture size nearest to n at which every stride-2 layer of a five-stage network sees an odd size: 32a + 1
    (224 -> 225: 225, 113, 57, 29, 15). There TF-"SAME" padding is the same on both sides, the only padding the NPU
    has ("padL != padR" otherwise); a pixel more or less does not change what a fully convolutional network sees."""
    return 32 * max(1, int(round((n - 1) / 32.0))) + 1


def versions():
    out = {}
    for module in ("torch", "torchvision", "timm", "ultralytics", "onnx", "onnxslim"):
        try:
            out[module] = __import__(module).__version__
        except Exception:  # noqa: BLE001 - not every export needs every library
            pass
    return out


def main(args):
    global PLAIN, HARD_SIGMOID_AS_RELU6
    add_site(args.site, args.config)
    PLAIN, HARD_SIGMOID_AS_RELU6 = args.plain, args.relu6
    target = os.path.abspath(args.onnx)
    os.makedirs(os.path.dirname(target), exist_ok=True)
    if os.path.exists(target):
        os.remove(target)
    # Ultralytics and onnxslim leave files where they run: a working folder next to the answer
    work = os.path.join(os.path.dirname(os.path.abspath(args.out)), "work")
    os.makedirs(work, exist_ok=True)
    cwd = os.getcwd()
    start = time.time()
    os.chdir(work)
    try:
        c = {"lib": args.lib, "name": args.name, "task": args.task, "size": args.size}
        params, fixes = export_one(c, target, args.opset, weights=args.weights, weights_tag=args.weights_tag)
    finally:
        os.chdir(cwd)
    ok = os.path.exists(target)
    return {
        "ok": ok, "onnx": target, "bytes": os.path.getsize(target) if ok else 0, "params": params, "fixes": fixes,
        "plain": PLAIN, "relu6": HARD_SIGMOID_AS_RELU6, "size": args.size, "weights": args.weights is not None,
        "seconds": round(time.time() - start, 1), "versions": versions(), "problems": [] if ok else ["no ONNX file was written"],
    }


if __name__ == "__main__":
    from _common import parser, run
    ap = parser("Exports one model of the model zoo to ONNX (design section 5.9).")
    ap.add_argument("--site", required=True, help="the zoo component's packages (<components>/zoo/site)")
    ap.add_argument("--config", help="folder for Ultralytics' settings")
    ap.add_argument("--lib", required=True, choices=["torchvision", "timm", "ultralytics"])
    ap.add_argument("--name", required=True)
    ap.add_argument("--task", required=True, choices=["class", "obj", "seg"])
    ap.add_argument("--size", required=True, type=int)
    ap.add_argument("--plain", action="store_true")
    ap.add_argument("--relu6", action="store_true")
    ap.add_argument("--weights")
    ap.add_argument("--weights-tag", dest="weights_tag")
    ap.add_argument("--onnx", required=True)
    ap.add_argument("--opset", type=int, default=13)
    sys.exit(run(main, ap.parse_args()))
