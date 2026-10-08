# SPDX-License-Identifier: GPL-3.0-or-later
"""The independent audit must reject plausible but corrupted final JSON."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

from property_range_audit import Audit, exit_code, function17_values, placeholders


class PropertyRangeAuditTests(unittest.TestCase):
    def fixture(self, root, low=3, high=6, actual=None, function=14, code="sock"):
        source = root / "source"
        output = root / "output"
        source.mkdir()
        (output / "keyed").mkdir(parents=True)
        (output / "extras").mkdir()
        (source / "properties.txt").write_text(
            f"code\tfunc1\tstat1\n{code}\t{function}\titem_numsockets\n", encoding="utf-8")
        (source / "itemstatcost.txt").write_text(
            "Stat\tdescfunc\tdescstrpos\nitem_numsockets\t29\tSocketable\n", encoding="utf-8")
        low_text = "" if low is None else str(low)
        high_text = "" if high is None else str(high)
        (source / "uniqueitems.txt").write_text(
            f"index\tlvl\tspawnable\tprop1\tmin1\tmax1\nFixture\t40\t1\t{code}\t{low_text}\t{high_text}\n", encoding="utf-8")
        (output / "extras/source-report.txt").write_text("\n".join(
            f"  [mod] global/excel/{name}  {source / name}"
            for name in ["properties.txt", "itemstatcost.txt", "uniqueitems.txt"]), encoding="utf-8")
        payload = [{"Index":"Fixture", "ItemLevel":40, "Lines":actual if actual is not None else
                    [{"key":"strSocketedCount", "args":[low,high]}]}]
        (output / "keyed/uniques.json").write_text(json.dumps(payload), encoding="utf-8")
        self.manifest(output)
        return output

    def manifest(self, output):
        files = []
        for path in sorted(output.rglob("*")):
            if not path.is_file() or path.name == "manifest.json":
                continue
            name = path.relative_to(output).as_posix()
            payload = path.read_bytes()
            files.append(dict(Path=name, Bytes=len(payload), Sha256=hashlib.sha256(payload).hexdigest(), Producer="fixture"))
        (output / "manifest.json").write_text(json.dumps(dict(SchemaVersion=2, Succeeded=True, Complete=True, Files=files)), encoding="utf-8")

    def run_audit(self, output, config=None):
        config = config or Path(__file__).resolve().parents[1] / "D2RMultiExport.Lib/Config/export-config.json"
        audit = Audit(output, config)
        audit.collect()
        return audit.evaluate()

    def independent_fixture(self, root, tables, documents, strings):
        """Literal source cells and hand-authored JSON; no exporter invocation."""
        source, output = root / "source", root / "output"
        source.mkdir()
        (output / "extras").mkdir(parents=True)
        for name, rows in tables.items():
            headers = list(dict.fromkeys(k for row in rows for k in row))
            text = "\t".join(headers) + "\n"
            text += "".join("\t".join(str(row[k]) if row.get(k) is not None else "" for k in headers) + "\n" for row in rows)
            (source / (name + ".txt")).write_text(text, encoding="utf-8")
        (output / "extras/source-report.txt").write_text("\n".join(
            f"  [mod] global/excel/{name}.txt  {source / (name + '.txt')}" for name in tables), encoding="utf-8")
        for name, data in {**documents, "strings/enUS.json": strings}.items():
            path = output / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(data), encoding="utf-8")
        self.manifest(output)
        return output

    def function17_fixture(self, root, parameter="", low=None, high=None, args=None,
                           code="counter", key="counterKey", template="Counter: %d / 6", desc=19, function=17, descval=None):
        stat = dict(Stat="counter_stat", descfunc=desc, descstrpos=key, descstrneg=key)
        if descval is not None:
            stat["descval"] = descval
        tables = {
            "properties": [dict(code=code, func1=function, stat1="counter_stat")],
            "itemstatcost": [stat],
            "uniqueitems": [dict(index="Fixture", lvl=40, prop1=code, par1=parameter, min1=low, max1=high)],
        }
        documents = {"keyed/uniques.json": [dict(Index="Fixture", ItemLevel=40,
            Lines=[dict(key=key, args=args)] if args is not None else [])]}
        return self.independent_fixture(root, tables, documents, {key: template})

    def test_function17_guide_precedence_fallback_and_signed_endpoints(self):
        cases = [("7", 2, 9, [7], "Parameter"), ("0", -3, 6, [0], "Parameter"),
            ("-4", 3, 9, [-4], "Parameter"), (None, None, None, [0], "DefaultZero"),
            ("", 0, 0, [0], "Endpoints"), ("", -3, -3, [-3], "Endpoints"),
            ("", -3, 0, [-3, 0], "Endpoints"), ("", 20, 30, [20, 30], "Endpoints"),
            ("", None, -2, [-2], "Endpoints"), ("", 2, None, [2], "Endpoints")]
        for parameter, low, high, expected, origin in cases:
            with self.subTest(parameter=parameter, low=low, high=high), tempfile.TemporaryDirectory() as root:
                self.assertEqual((expected, origin), function17_values(parameter, low, high))
                output = self.function17_fixture(Path(root), parameter, low, high, expected)
                occurrence = self.run_audit(output)["Occurrences"][0]
                self.assertEqual("Verified", occurrence["Status"])
                self.assertEqual(expected, occurrence["StatValues"])
                self.assertEqual(expected, occurrence["Expected"])
                self.assertEqual(origin, occurrence["ValueSource"])
                self.assertEqual("Add", occurrence["StatOperation"])

    def test_function17_malformed_parameter_and_unknown_descriptions_are_coverage_gaps(self):
        for parameter, desc, template in [("skillname", 19, "%d"), ("", 18, "%d"), ("", 19, "%d %s")]:
            with self.subTest(parameter=parameter, desc=desc), tempfile.TemporaryDirectory() as root:
                output = self.function17_fixture(Path(root), parameter, 1, 1, [1], desc=desc, template=template)
                report = self.run_audit(output)
                self.assertEqual({"Unsupported": 1}, report["Counts"])
                self.assertEqual(2, exit_code(report))

    def test_function17_rejects_changed_missing_and_collapsed_endpoints_and_wrong_keys(self):
        for args, status in [([20], "CollapsedRange"), ([30], "IncorrectValues"), ([20, 29], "IncorrectValues"),
                             ([21, 30], "IncorrectValues"), ([30, 20], "IncorrectValues"),
                             ([-20, -30], "IncorrectValues"), ([20, 30, 40], "IncorrectValues"),
                             ([20, 30, "counterKey"], "IncorrectValues"), ([], "IncorrectValues"), (None, "MissingProperty")]:
            with self.subTest(args=args), tempfile.TemporaryDirectory() as root:
                output = self.function17_fixture(Path(root), low=20, high=30, args=args, code="stacked/gem", key="StackedGem")
                report = self.run_audit(output)
                self.assertEqual({status: 1}, report["Counts"])
                self.assertEqual(1, exit_code(report))
        with tempfile.TemporaryDirectory() as root:
            output = self.function17_fixture(Path(root), low=20, high=30, args=[20, 30])
            self.rewrite_document(output, "keyed/uniques.json", lambda records: records[0]["Lines"][0].update(key="similarCounterKey"))
            self.assertEqual({"MissingProperty": 1}, self.run_audit(output)["Counts"])

    def rewrite_document(self, output, name, mutate):
        path = output / name
        data = json.loads(path.read_text(encoding="utf-8"))
        mutate(data)
        path.write_text(json.dumps(data), encoding="utf-8")
        self.manifest(output)

    def test_signed_gem_and_keychain_credits_and_debits(self):
        for code, key in [("stacked/gem", "StackedGem"), ("stacked/terror-key", "StackedTerrorKey"),
                          ("stacked/hate-key", "StackedHateKey"), ("stacked/destruction-key", "StackedDestructionKey")]:
            for value in (-200, -1, 0, 3):
                for actual in (value, value+1, -value if value else 1):
                    with self.subTest(code=code, value=value, actual=actual), tempfile.TemporaryDirectory() as root:
                        output = self.function17_fixture(Path(root), low=value, high=value, args=[actual], code=code, key=key)
                        self.assertEqual({"Verified" if actual == value else "IncorrectValues": 1}, self.run_audit(output)["Counts"])

    def test_value_less_replenish_fade_and_boolean_templates(self):
        for code, key, template, function, parameter, low in [
            ("rep-quant", "ModStre9v", "Replenishes quantity", 17, "25", None),
            ("fade", "fadeDescription", "You feel incorporeal...", 17, "", 1),
            ("nofreeze", "ModStr5z", "Cannot Be Frozen", 1, "", None),
            ("half-freeze", "ModStr4b", "Half Freeze Duration", 1, "", None)]:
            for actual in ([], [999]):
                with self.subTest(code=code, actual=actual), tempfile.TemporaryDirectory() as root:
                    output = self.function17_fixture(Path(root), parameter, low, low, actual,
                        code=code, key=key, template=template, function=function)
                    report = self.run_audit(output)
                    self.assertEqual({"Verified" if not actual else "IncorrectValues": 1}, report["Counts"])
                    self.assertEqual([], report["Occurrences"][0]["Expected"])
            with tempfile.TemporaryDirectory() as root:
                output = self.function17_fixture(Path(root), parameter, low, low, None,
                    code=code, key=key, template=template, function=function)
                self.assertEqual({"MissingProperty": 1}, self.run_audit(output)["Counts"])
        self.assertEqual(["d", "s"], placeholders("%+d %s 100%%"))
        self.assertEqual([], placeholders("100%% credit"))

    def test_function17_live_stat_label_allows_only_its_unused_configured_endpoint(self):
        # lab_kill_display: descfunc 3 / descval 2 appends the player's live stat after
        # a slotless label, so the configured endpoint is never the displayed count.
        label = dict(code="lab_kill_display", key="boss_kill_str", template="Labryinth Boss Souls:", desc=3)
        for parameter, low, descval, args, status in [
            ("", 1, 2, [], "Verified"), ("", 1, 2, [1], "Verified"), ("", 1, 2, [2], "IncorrectValues"),
            ("", 1, 1, [1], "IncorrectValues"), ("", 1, 0, [1], "IncorrectValues"), ("", 1, None, [1], "IncorrectValues"),
            ("1", None, 2, [1], "IncorrectValues"), ("1", None, 2, [], "Verified")]:
            with self.subTest(parameter=parameter, descval=descval, args=args), tempfile.TemporaryDirectory() as root:
                output = self.function17_fixture(Path(root), parameter, low, low, args, descval=descval, **label)
                report = self.run_audit(output)
                self.assertEqual({status: 1}, report["Counts"])
                self.assertEqual([], report["Occurrences"][0]["Expected"])
        # Unrelated function-17 value-less lines keep rejecting unused arguments.
        with tempfile.TemporaryDirectory() as root:
            output = self.function17_fixture(Path(root), "", 1, 1, [1], descval=2, code="fade",
                key="fadeDescription", template="You feel incorporeal...", desc=19)
            self.assertEqual({"IncorrectValues": 1}, self.run_audit(output)["Counts"])

    def test_no_function1_parameter_fallback_and_malformed_numeric_arguments(self):
        with tempfile.TemporaryDirectory() as root:
            output = self.function17_fixture(Path(root), "25", args=[], code="att", key="ModStr1h", template="%+d to Attack Rating", function=1)
            report = self.run_audit(output)
            self.assertEqual({"IncorrectValues": 1}, report["Counts"])
            self.assertEqual([], report["Occurrences"][0]["Expected"])
            self.assertEqual(1, exit_code(report))
        # The exporter's omission of that unrenderable line is unresolved source data, not a pass.
        with tempfile.TemporaryDirectory() as root:
            output = self.function17_fixture(Path(root), "25", code="att", key="ModStr1h", template="%+d to Attack Rating", function=1)
            report = self.run_audit(output)
            self.assertEqual({"Unevaluated": 1}, report["Counts"])
            self.assertIn("line omitted", report["Occurrences"][0]["Reason"])
            self.assertEqual(2, exit_code(report))

    def test_function17_requires_current_manifested_localization_template(self):
        with tempfile.TemporaryDirectory() as root:
            output = self.function17_fixture(Path(root), low=1, high=1, args=[1])
            path = output / "manifest.json"
            manifest = json.loads(path.read_text())
            manifest["Files"] = [f for f in manifest["Files"] if f["Path"] != "strings/enUS.json"]
            path.write_text(json.dumps(manifest))
            self.assertEqual({"Unsupported": 1}, self.run_audit(output)["Counts"])

    def cube_fixture(self, root, code, key, template, args, output_scope="A", final_item=False):
        # Eligibility's counter reference is deliberately different from the
        # output modifier. Contributions come only from the modifier cells.
        rows = [{"description": "Increment", "enabled": 1, "op": 18, "param": 999,
            "value": 0, "input 1": "charm", "output": "useitem", "mod 1": code,
            "mod 1 min": 1, "mod 1 max": 1}]
        documents = [dict(Index=1, Outputs={output_scope: dict(Lines=[dict(key=key, args=args)])})]
        if final_item:
            rows.append(dict(description="Final transformation", enabled=1, op=18, param=999,
                value=1, output="newcharm"))
            documents.append(dict(Index=2, Outputs={"A": dict(Lines=[])}))
        tables = {"properties": [dict(code=code, func1=17, stat1="counter_stat")],
            "itemstatcost": [dict(Stat="counter_stat", descfunc=19, descstrpos=key, descstrneg=key)],
            "cubemain": rows}
        return self.independent_fixture(root, tables, {"keyed/cube-recipes.json": documents}, {key: template})

    def test_all_upgrade_families_validate_contributions_without_inferred_cap_recipes(self):
        for code, key, cap in [("upgrade/major", "MajorUpgrades", 5), ("upgrade/medium", "MediumUpgrades", 3),
            ("upgrade/minor", "MinorUpgrades", 1), ("upgrade/uber", "UberUpgrades", 10),
            ("upgrade/uberCharmSmall", "UberCharmSmallUpgrades", 6), ("upgrade/uberCharmLarge", "UberCharmLargeUpgrades", 10)]:
            for value in (1, 2):
                with self.subTest(code=code, value=value), tempfile.TemporaryDirectory() as root:
                    output = self.cube_fixture(Path(root), code, key, f"Upgrades: %d / {cap}", [value], final_item=True)
                    report = self.run_audit(output)
                    self.assertEqual({"Verified" if value == 1 else "IncorrectValues": 1}, report["Counts"])
                    self.assertEqual([1], report["Occurrences"][0]["StatValues"])
                    self.assertEqual(1, len(report["Occurrences"]))

    def test_counter_in_wrong_recipe_or_output_scope_cannot_pass(self):
        for corruption in ("other_recipe", "B", "C", "name", "qualifier"):
            with self.subTest(corruption=corruption), tempfile.TemporaryDirectory() as root:
                output = self.cube_fixture(Path(root), "upgrade/major", "MajorUpgrades", "%d / 5", [1])
                def mutate(records):
                    line = records[0]["Outputs"]["A"]["Lines"].pop()
                    if corruption == "other_recipe":
                        records.append(dict(Index=2, Outputs={"A": dict(Lines=[line])}))
                    elif corruption == "name":
                        records[0]["Outputs"]["A"]["Name"] = line
                    elif corruption == "qualifier":
                        records[0]["Outputs"]["A"]["Lines"] = [{**line, "qualifier": "weapon"}]
                    else:
                        records[0]["Outputs"][corruption] = dict(Lines=[line])
                self.rewrite_document(output, "keyed/cube-recipes.json", mutate)
                self.assertEqual({"MissingProperty": 1}, self.run_audit(output)["Counts"])

    def test_counter_in_other_item_equipment_argument_or_group_is_not_a_property_match(self):
        for corruption in ("other_item", "equipment", "argument", "group"):
            with self.subTest(corruption=corruption), tempfile.TemporaryDirectory() as root:
                output = self.function17_fixture(Path(root), low=1, high=1, args=[1])
                def mutate(records):
                    line = records[0]["Lines"].pop()
                    if corruption == "other_item":
                        records.append(dict(Index="Different Fixture", Lines=[line]))
                    elif corruption == "equipment":
                        records[0]["Equipment"] = dict(Lines=[line])
                    elif corruption == "argument":
                        records[0]["Lines"] = [dict(key="outer", args=[line])]
                    else:
                        records[0]["Lines"] = [dict(code="unrelated", children=[line])]
                self.rewrite_document(output, "keyed/uniques.json", mutate)
                self.assertEqual({"MissingProperty": 1}, self.run_audit(output)["Counts"])

    def family_fixture(self, root, family, line):
        tables = {"properties": [dict(code="counter", func1=17, stat1="counter_stat")],
            "itemstatcost": [dict(Stat="counter_stat", descfunc=19, descstrpos="counterKey")]}
        scope = {}
        if family == "uniqueitems":
            tables[family] = [dict(index="Fixture", lvl=40, prop1="counter", min1=1, max1=1)]
            documents = {"keyed/uniques.json": [dict(Index="Fixture", Lines=[line])]}
        elif family == "setitems":
            tables[family] = [{"index": "Fixture", "lvl": 40, "add func": 2,
                "aprop1a": "counter", "amin1a": 1, "amax1a": 1}]
            documents = {"keyed/sets.json": [dict(Index="Set", SetItems=[dict(Index="Fixture", SetBonuses=[[line]])])]}
            scope = dict(itemsRequired=2)
        elif family in ("sets_partial", "sets_full"):
            partial = family == "sets_partial"
            tables["sets"] = [dict(index="Fixture", **({"pcode2a": "counter", "pmin2a": 1, "pmax2a": 1} if partial else
                {"fcode1": "counter", "fmin1": 1, "fmax1": 1}))]
            documents = {"keyed/sets.json": [dict(Index="Fixture", SetItems=[], **{"PartialBonuses" if partial else "FullBonuses": [line]})]}
            scope = dict(itemsRequired=2) if partial else dict(fullSet=True)
        elif family in ("runes", "gems"):
            tables["runes"] = [dict(name="Fixture", complete=1, itype1="weap", rune1="r01",
                **(dict(t1code1="counter", t1min1=1, t1max1=1) if family == "runes" else {}))]
            if family == "gems":
                tables["gems"] = [dict(code="r01", weaponmod1code="counter", weaponmod1min=1, weaponmod1max=1)]
            documents = {"keyed/runewords.json": [dict(Index="Fixture", Lines=[line])]}
        elif family in ("magicprefix", "magicsuffix", "automagic"):
            tables[family] = [dict(name="Fixture", spawnable=1, level=1, levelreq=1, group=304,
                mod1code="counter", mod1min=1, mod1max=1)]
            if family == "automagic":
                tables["armor"] = [dict(code="host", autoprefix=304)]
                tables["weapons"] = []
                documents = {"keyed/armors.json": [dict(NameKey="host", AutoMagicGroups=[
                    dict(Code="Fixture", Level=1, RequiredLevel=1, Lines=[line])])]}
            else:
                documents = {f"keyed/{family}.json": [dict(NameKey="Fixture", Lines=[line])]}
        elif family == "propertygroups":
            tables["uniqueitems"] = [dict(index="Fixture", lvl=40, prop1="choice")]
            tables[family] = [dict(code="choice", prop1="counter", modmin1=1, modmax1=1, chance1=20)]
            documents = {"keyed/uniques.json": [dict(Index="Fixture", Lines=[
                dict(code="choice", key="strPropertyGroupsProperty", children=[line])])]}
            scope = dict(chance=20)
        else:
            raise AssertionError(family)
        line.update(scope)
        return self.independent_fixture(root, tables, documents, {"counterKey": "%d"})

    def test_function17_in_each_supported_family_rejects_missing_or_incorrect_values(self):
        for family in ("uniqueitems", "setitems", "sets_partial", "sets_full", "runes", "gems", "magicprefix", "magicsuffix", "automagic", "propertygroups"):
            for actual in (1, 2):
                with self.subTest(family=family, actual=actual), tempfile.TemporaryDirectory() as root:
                    output = self.family_fixture(Path(root), family, dict(key="counterKey", args=[actual]))
                    report = self.run_audit(output)
                    occurrences = [o for o in report["Occurrences"] if o["Code"] == "counter" and o["Target"]]
                    self.assertEqual(1, len(occurrences))
                    self.assertEqual("Verified" if actual == 1 else "IncorrectValues", occurrences[0]["Status"])
                    if actual != 1:
                        self.assertEqual(1, exit_code(report))

    def test_function17_activation_scope_and_property_group_chance(self):
        for family, field, wrong in [("setitems", "itemsRequired", 3), ("sets_partial", "itemsRequired", 3),
                                    ("sets_full", "fullSet", False), ("propertygroups", "chance", 30)]:
            with self.subTest(family=family), tempfile.TemporaryDirectory() as root:
                output = self.family_fixture(Path(root), family, dict(key="counterKey", args=[1]))
                file = "keyed/uniques.json" if family == "propertygroups" else "keyed/sets.json"
                def mutate(records):
                    if family == "setitems":
                        line = records[0]["SetItems"][0]["SetBonuses"][0][0]
                    elif family == "propertygroups":
                        line = records[0]["Lines"][0]["children"][0]
                    else:
                        line = records[0]["PartialBonuses" if family == "sets_partial" else "FullBonuses"][0]
                    line[field] = wrong
                self.rewrite_document(output, file, mutate)
                self.assertIn("MissingProperty", self.run_audit(output)["Counts"])

    def test_each_family_rejects_wrong_property_keys_and_missing_property_lines(self):
        for family in ("uniqueitems", "setitems", "sets_partial", "sets_full", "runes", "gems", "magicprefix", "magicsuffix", "automagic", "propertygroups"):
            for corruption in ("wrong_key", "missing"):
                with self.subTest(family=family, corruption=corruption), tempfile.TemporaryDirectory() as root:
                    output = self.family_fixture(Path(root), family, dict(key="counterKey", args=[1]))
                    def mutate(value):
                        if isinstance(value, list):
                            if corruption == "missing":
                                value[:] = [v for v in value if not isinstance(v, dict) or v.get("key") != "counterKey"]
                            for child in value:
                                mutate(child)
                        elif isinstance(value, dict):
                            if corruption == "wrong_key" and value.get("key") == "counterKey":
                                value["key"] = "similarlyNamedKey"
                            for child in value.values():
                                mutate(child)
                    for path in (output / "keyed").glob("*.json"):
                        self.rewrite_document(output, path.relative_to(output).as_posix(), mutate)
                    report = self.run_audit(output)
                    occurrence = next(o for o in report["Occurrences"] if o["Code"] == "counter" and o["Target"])
                    self.assertEqual("MissingProperty", occurrence["Status"])
                    self.assertEqual(1, exit_code(report))

    def test_function17_sign_selects_the_actual_description_key(self):
        with tempfile.TemporaryDirectory() as root:
            tables = {"properties": [dict(code="counter", func1=17, stat1="counter_stat")],
                "itemstatcost": [dict(Stat="counter_stat", descfunc=19, descstrpos="positiveKey", descstrneg="negativeKey")],
                "uniqueitems": [dict(index="Fixture", par1=-4, prop1="counter", min1=2, max1=9)]}
            output = self.independent_fixture(Path(root), tables, {"keyed/uniques.json": [dict(Index="Fixture",
                Lines=[dict(key="negativeKey", args=[-4])])]}, {"negativeKey": "%d", "positiveKey": "%d"})
            self.assertEqual({"Verified": 1}, self.run_audit(output)["Counts"])
            self.rewrite_document(output, "keyed/uniques.json", lambda records: records[0]["Lines"][0].update(key="positiveKey"))
            self.assertEqual({"MissingProperty": 1}, self.run_audit(output)["Counts"])

    def test_function17_counters_do_not_accept_boolean_numeric_arguments(self):
        with tempfile.TemporaryDirectory() as root:
            output = self.function17_fixture(Path(root), low=1, high=1, args=[True])
            self.assertEqual({"IncorrectValues": 1}, self.run_audit(output)["Counts"])

    def test_existing_function17_scaling_and_repair_representations(self):
        for parameter, low, high, expected in [("3", 8, 12, [0.375]), ("0", 8, 12, [0]),
            ("", 8, 12, [1, 1.5]), ("", -3, -3, [-0.375])]:
            with self.subTest(parameter=parameter, low=low), tempfile.TemporaryDirectory() as root:
                root = Path(root)
                tables = {"properties": [dict(code="scaling", func1=17, stat1="bonus_perlevel")],
                    "itemstatcost": [{"Stat": "bonus_perlevel", "descfunc": 19, "descstrpos": "growthKey", "op param": 3}],
                    "uniqueitems": [dict(index="Fixture", prop1="scaling", par1=parameter, min1=low, max1=high)]}
                output = self.independent_fixture(root, tables, {"keyed/uniques.json": [dict(Index="Fixture",
                    Lines=[dict(key="growthKey", args=expected, perLevel=True)])]}, {"growthKey": "%d"})
                self.assertEqual({"Verified": 1}, self.run_audit(output)["Counts"])
                self.rewrite_document(output, "keyed/uniques.json", lambda records: records[0]["Lines"][0].update(perLevel=False))
                self.assertEqual({"IncorrectValues": 1}, self.run_audit(output)["Counts"])
        for parameter, key, args in [("1", "ModStre9t", [1]), ("7", "ModStre9u", [1, 7]), ("0", "ModStre9t", [0])]:
            with self.subTest(repair_parameter=parameter), tempfile.TemporaryDirectory() as root:
                tables = {"properties": [dict(code="repair", func1=17, stat1="item_repair")],
                    "itemstatcost": [dict(Stat="item_repair", descfunc=11, descstrpos="ModStre9t", descstr2="ModStre9u")],
                    "uniqueitems": [dict(index="Fixture", prop1="repair", par1=parameter)]}
                output = self.independent_fixture(Path(root), tables, {"keyed/uniques.json": [dict(Index="Fixture", Lines=[dict(key=key, args=args)])]},
                    {"ModStre9t": "Repairs %d durability per second", "ModStre9u": "Repairs %d durability in %d seconds"})
                self.assertEqual({"Verified": 1}, self.run_audit(output)["Counts"])

    def test_function17_additive_counters_and_parameter_contributions(self):
        for parameter, low, high, expected in [("2", 10, 20, [4]), ("", -3, 2, [-6, 4]), ("", 1, 1, [2])]:
            for actual in (expected, [99]):
                with self.subTest(parameter=parameter, actual=actual), tempfile.TemporaryDirectory() as root:
                    output = self.function17_fixture(Path(root), parameter, low, high, actual)
                    path = Path(root) / "source/uniqueitems.txt"
                    path.write_text("index\tlvl\tprop1\tpar1\tmin1\tmax1\tprop2\tpar2\tmin2\tmax2\n"
                        f"Fixture\t40\tcounter\t{parameter}\t{low}\t{high}\tcounter\t{parameter}\t{low}\t{high}\n", encoding="utf-8")
                    report = self.run_audit(output)
                    self.assertEqual({"Verified" if actual == expected else "IncorrectValues": 2}, report["Counts"])
                    self.assertTrue(all(o["Expected"] == expected for o in report["Occurrences"]))

    def test_centralized_ignored_codes_and_merge_blocklist_are_reused(self):
        config_path = Path(__file__).resolve().parents[1] / "D2RMultiExport.Lib/Config/export-config.json"
        config = json.loads(config_path.read_text(encoding="utf-8-sig"))
        for code in config["ignoredPropertyCodes"]:
            with self.subTest(code=code), tempfile.TemporaryDirectory() as root:
                output = self.function17_fixture(Path(root), low=1, high=1, code=code.upper())
                self.assertEqual({"Excluded": 1}, self.run_audit(output)["Counts"])
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            output = self.function17_fixture(root, low=1, high=1, args=[1])
            source = root / "source/uniqueitems.txt"
            source.write_text("index\tlvl\tprop1\tmin1\tmax1\tprop2\tmin2\tmax2\nFixture\t40\tcounter\t1\t1\tcounter\t1\t1\n", encoding="utf-8")
            config["propertyMergeBlocklist"].append("COUNTER")
            local_config = root / "export-config.json"
            local_config.write_text(json.dumps(config), encoding="utf-8")
            (root / "stat-overrides.json").write_bytes(config_path.with_name("stat-overrides.json").read_bytes())
            self.assertEqual({"Verified": 2}, self.run_audit(output, local_config)["Counts"])

    def test_metadata_driven_hidden_stat_and_parameter_only_duration_exclusions(self):
        for code, stat, low, high, parameter in [("corruptedDummy", "item_corruptedDummy", 1, 100, ""),
            ("unlistedHelper", "unlisted_helper", -1, 0, ""), ("cold-len", "coldlength", None, None, "300")]:
            with self.subTest(code=code), tempfile.TemporaryDirectory() as root:
                tables = {"properties": [dict(code=code, func1=1, stat1=stat)], "itemstatcost": [dict(Stat=stat)],
                    "uniqueitems": [dict(index="Fixture", prop1=code, par1=parameter, min1=low, max1=high)]}
                output = self.independent_fixture(Path(root), tables, {"keyed/uniques.json": [dict(Index="Fixture", Lines=[])]}, {})
                self.assertEqual({"IntentionalNonDisplayStat": 1}, self.run_audit(output)["Counts"])

    def test_automagic_reference_definitions_require_a_source_host_before_omission_is_a_gap(self):
        for referenced in (False, True):
            with self.subTest(referenced=referenced), tempfile.TemporaryDirectory() as root:
                output = self.family_fixture(Path(root), "automagic", dict(key="counterKey", args=[1]))
                if not referenced:
                    (Path(root) / "source/armor.txt").write_text("code\tAutoPrefix\nhost\t0\n", encoding="utf-8")
                self.rewrite_document(output, "keyed/armors.json", lambda records: records.clear())
                report = self.run_audit(output)
                self.assertEqual({"NotExported" if referenced else "UnreferencedAutomagicDefinition": 1}, report["Counts"])
                self.assertEqual(int(referenced), report["UnresolvedEligibility"])

    def test_every_attached_automagic_host_must_have_the_expected_property(self):
        with tempfile.TemporaryDirectory() as root:
            output = self.family_fixture(Path(root), "automagic", dict(key="counterKey", args=[1]))
            def mutate(records):
                records.append(dict(NameKey="otherhost", AutoMagicGroups=[dict(Code="Fixture", Level=1, RequiredLevel=1, Lines=[])]))
            self.rewrite_document(output, "keyed/armors.json", mutate)
            self.assertEqual({"MissingProperty": 1}, self.run_audit(output)["Counts"])

    def test_distinct_zero_negative_equal_and_missing_endpoints(self):
        for low, high, expected in [(3,6,[3,6]), (0,2,[0,2]), (-3,0,[-3,0]), (2,2,[2]), (None,3,[3]), (3,None,[3])]:
            with self.subTest(low=low, high=high), tempfile.TemporaryDirectory() as root:
                output = self.fixture(Path(root),low,high,[dict(key="strSocketedCount",args=expected)])
                report = self.run_audit(output)
                self.assertEqual({"Verified":1},report["Counts"])
                self.assertEqual(low,report["Occurrences"][0]["Min"])
                self.assertEqual(high,report["Occurrences"][0]["Max"])

    def test_corrupted_wire_values_and_missing_properties_are_detected(self):
        for actual, status in [([3],"CollapsedRange"), ([3,5],"IncorrectValues"), ([3,6,9],"IncorrectValues"), ([],"IncorrectValues")]:
            with self.subTest(actual=actual), tempfile.TemporaryDirectory() as root:
                output = self.fixture(Path(root),actual=[dict(key="strSocketedCount",args=actual)])
                self.assertEqual({status:1},self.run_audit(output)["Counts"])
        with tempfile.TemporaryDirectory() as root:
            output = self.fixture(Path(root),actual=[])
            self.assertEqual({"MissingProperty":1},self.run_audit(output)["Counts"])

    def test_unknown_function_is_not_a_pass_even_when_numbers_match(self):
        with tempfile.TemporaryDirectory() as root:
            output = self.fixture(Path(root),function=99)
            occurrence = self.run_audit(output)["Occurrences"][0]
            self.assertEqual("Unsupported",occurrence["Status"])
            self.assertEqual([99],occurrence["Functions"])

    def test_mutated_payload_cannot_pass_using_stale_manifest(self):
        with tempfile.TemporaryDirectory() as root:
            output = self.fixture(Path(root))
            (output / "keyed/uniques.json").write_text("[]", encoding="utf-8")
            with self.assertRaisesRegex(ValueError,"integrity mismatch"):
                self.run_audit(output)

    def test_stale_file_not_in_manifest_cannot_satisfy_property(self):
        with tempfile.TemporaryDirectory() as root:
            output = self.fixture(Path(root))
            manifest = json.loads((output / "manifest.json").read_text(encoding="utf-8"))
            manifest["Files"] = [f for f in manifest["Files"] if f["Path"]!="keyed/uniques.json"]
            (output / "manifest.json").write_text(json.dumps(manifest),encoding="utf-8")
            self.assertEqual({"NotExported":1},self.run_audit(output)["Counts"])

    def test_equal_damage_components_require_two_final_slots(self):
        for args, status in [([7,7], "Verified"), ([7], "IncorrectValues")]:
            with self.subTest(args=args), tempfile.TemporaryDirectory() as root:
                output = self.fixture(Path(root), low=7, high=7, actual=[dict(key="damageKey",args=args)], code="damage")
                source = Path(root) / "source"
                (source / "properties.txt").write_text(
                    "code\tfunc1\tfunc2\tstat1\tstat2\ndamage\t15\t16\tfiremindam\tfiremaxdam\n", encoding="utf-8")
                (source / "itemstatcost.txt").write_text(
                    "Stat\tdescfunc\tdescstrpos\nfiremindam\t31\tdamageKey\n", encoding="utf-8")
                self.assertEqual({status:1}, self.run_audit(output)["Counts"])

    def test_independent_event_chance_and_level_are_not_a_random_range(self):
        for args, status in [([5,12,"42"], "Verified"), ([5,"42"], "IncorrectValues")]:
            with self.subTest(args=args), tempfile.TemporaryDirectory() as root:
                output = self.fixture(Path(root), low=5, high=12, actual=[dict(key="eventKey",args=args)], code="event")
                source = Path(root) / "source"
                (source / "properties.txt").write_text("code\tfunc1\tstat1\nevent\t11\titem_skillonhit\n", encoding="utf-8")
                (source / "itemstatcost.txt").write_text("Stat\tdescfunc\tdescstrpos\nitem_skillonhit\t15\teventKey\n", encoding="utf-8")
                (source / "uniqueitems.txt").write_text(
                    "index\tlvl\tprop1\tpar1\tmin1\tmax1\nFixture\t40\tevent\t42\t5\t12\n", encoding="utf-8")
                self.assertEqual({status:1}, self.run_audit(output)["Counts"])

    def test_missing_sources_and_unmatched_records_remain_explicit(self):
        with tempfile.TemporaryDirectory() as root:
            output = self.fixture(Path(root), actual=[])
            (output / "keyed/uniques.json").write_text("[]", encoding="utf-8")
            self.manifest(output)
            report = self.run_audit(output)
            self.assertEqual(1, report["UnresolvedEligibility"])
            self.assertFalse(report["TableCoverage"]["runes.txt"]["Available"])
            self.assertEqual(2, exit_code(report))

    def test_failed_manifest_cannot_be_a_successful_audit(self):
        with tempfile.TemporaryDirectory() as root:
            output = self.fixture(Path(root))
            manifest = json.loads((output / "manifest.json").read_text(encoding="utf-8"))
            manifest["Succeeded"] = False
            (output / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
            self.assertEqual(1, exit_code(self.run_audit(output)))


if __name__ == "__main__":
    unittest.main()
