# SPDX-License-Identifier: GPL-3.0-or-later
"""Independent source-cell -> final JSON audit. Writes JSON to stdout only.

Input paths come exclusively from the export's SourceRoots source report. This
validator deliberately reads TSV independently of the production typed parser
and does not invoke PropertyMapper/PropertyKeyResolver to calculate expectations.
Unknown semantics are findings, never passes. See docs/PROPERTY-RANGES.md.
"""
import argparse
import collections
import hashlib
import json
import math
from pathlib import Path
import re
import sys


def number(value):
    if value is None or value == "":
        return None
    return int(value)


def endpoints(low, high):
    return [v for i, v in enumerate([low, high]) if v is not None and (i == 0 or v != low)]


def function17_values(parameter, low, high):
    """ItemModsSetParam, properties.js: parameter, endpoints, then zero.

    A present zero is a value. Invalid parameters are not silently treated as
    absent, and equal endpoints describe one fixed contribution.
    """
    value = number(parameter)
    if value is not None:
        return [value], "Parameter"
    values = endpoints(low, high)
    return (values, "Endpoints") if values else ([0], "DefaultZero")


def placeholders(template):
    # Ignore escaped percent signs. Keys are opaque; expectations come from
    # their actual localization templates, not from property-name similarity.
    return [m[-1] for m in re.findall(r"%%|%(?:\d+\$)?[-+ #0]*\d*(?:\.\d+)?[dsuf]", template) if m != "%%"]


def lines(value):
    if isinstance(value, dict):
        if "key" in value or "code" in value and "children" in value:
            yield value
            # Nested arguments and group children are not root property lines.
            # Children are selected explicitly in their source group scope.
            return
        for child in value.values():
            yield from lines(child)
    elif isinstance(value, list):
        for child in value:
            yield from lines(child)


class Audit:
    def __init__(self, output, config):
        self.output = Path(output)
        self.config = json.loads(Path(config).read_text(encoding="utf-8-sig"))
        for name in ("ignoredPropertyCodes", "propertyMergeBlocklist"):
            self.config[name] = {code.lower() for code in self.config[name]}
        self.config_hashes = {Path(config).name: hashlib.sha256(Path(config).read_bytes()).hexdigest()}
        self.manifest = json.loads((self.output / "manifest.json").read_text(encoding="utf-8-sig"))
        if self.manifest["SchemaVersion"] != 2:
            raise ValueError("Requires manifest schema 2")
        self.files = {f["Path"]: f for f in self.manifest["Files"]}
        # Verify every current-run payload before inspecting it. Stale files never
        # satisfy the audit. Manifest hashes do not attest unchanged source inputs.
        for relative, entry in self.files.items():
            path = (self.output / relative).resolve()
            if not path.is_relative_to(self.output.resolve()):
                raise ValueError(f"Manifest path escapes output: {relative}")
            payload = path.read_bytes()
            if len(payload) != entry["Bytes"] or hashlib.sha256(payload).hexdigest() != entry["Sha256"]:
                raise ValueError(f"Manifest integrity mismatch: {relative}")
        self.strings = self.document("strings/enUS.json") or {}
        self.sources = {}
        self.source_hashes = {}
        report = self.document("extras/source-report.txt", text=True)
        for line in report.splitlines():
            match = re.match(r"\s*\[(?:mod|casc|bundled)\] global/excel/(\S+)\s{2,}(.+)$", line)
            if match:
                self.sources[match[1].lower()] = Path(match[2])
        self.tables = {}
        self.properties = {r["code"].lower(): r for r in self.table("properties") if r.get("code")}
        self.stats = {r["stat"].lower(): r for r in self.table("itemstatcost") if r.get("stat")}
        override_path = Path(config).with_name("stat-overrides.json")
        overrides = json.loads(override_path.read_text(encoding="utf-8-sig"))
        self.config_hashes[override_path.name] = hashlib.sha256(override_path.read_bytes()).hexdigest()
        self.synthetic = {}
        for row in overrides["statOverrides"]:
            stat = self.stats.get(row.get("descStrPosKeyFromStat", "").lower(), {})
            self.synthetic[row["stat"].lower()] = {
                "descfunc": str(row["descFunc"]),
                "descstrpos": row.get("descStrPosKey") or stat.get("descstrpos", ""),
                "descstrneg": row.get("descStrNegKey") or stat.get("descstrneg", ""),
            }
        for row in overrides["statFixes"]:
            if row["stat"].lower() in self.stats:
                self.stats[row["stat"].lower()].update(descfunc=str(row["descFunc"]),
                    descstrpos=row["descStrPosKey"], descstrneg=row["descStrNegKey"])
        self.occurrences = []
        self.targets = {}
        self.property_targets = {}
        self.host_targets = {}
        self.output_targets = {}
        self.levels = {}
        self.groups = {r["code"].lower(): r for r in self.table("propertygroups") if r.get("code")}
        self.skills = {}
        descriptions = {r["skilldesc"]: r for r in self.table("skilldesc")}
        for row in self.table("skills"):
            if row.get("skill"):
                self.skills[row["skill"].lower()] = row
                self.skills[row.get("*id", "")] = row
                row["namekey"] = descriptions.get(row.get("skilldesc"), {}).get("str name") or row["skill"]
        self.charstats = self.table("charstats")
        self.types = {r["code"].lower(): r for r in self.table("itemtypes") if r.get("code")}
        self.equipment = {r["code"].lower(): r for table in ["armor", "weapons"] for r in self.table(table) if r.get("code")}
        self.raw_targets = {}

    def document(self, relative, text=False):
        if relative not in self.files:
            return "" if text else []
        data = (self.output / relative).read_text(encoding="utf-8-sig")
        return data if text else json.loads(data)

    def table(self, name):
        if name in self.tables:
            return self.tables[name]
        path = self.sources.get(name + ".txt")
        if path is None:
            self.tables[name] = []
        else:
            self.source_hashes[name + ".txt"] = hashlib.sha256(path.read_bytes()).hexdigest()
            # Literal splitting retains quotes in raw game cells (CSV quoting
            # rules would strip quotes used in cube tokens and calculations).
            text = path.read_text(encoding="utf-8-sig").splitlines()
            header = text[0].split("\t")
            self.tables[name] = [dict(zip([h.lower() for h in header], row.split("\t"))) for row in text[1:]]
        return self.tables[name]

    def add(self, table, record, row, slot, code, param, low, high, target, scope=None, reason=None, chance=None):
        if not code:
            return
        occurrence = dict(Table=table + ".txt", Record=record, Row=row + 2,
            Column=slot, Code=code, Parameter=param, Min=number(low), Max=number(high),
            Target=target, Scope=scope or {}, Chance=number(chance), Reason=reason)
        self.occurrences.append(occurrence)
        if code.lower() in self.groups and target:
            group = self.groups[code.lower()]
            for i in range(1, 9):
                pmin, pmax = group.get(f"parmin{i}"), group.get(f"parmax{i}")
                par = f"{pmin}-{pmax}" if pmin and pmax and pmin != pmax else pmin or pmax
                self.add("propertygroups", group["code"], self.table("propertygroups").index(group),
                    f"Prop{i}", group.get(f"prop{i}"), par, group.get(f"modmin{i}"), group.get(f"modmax{i}"),
                    target, {**(scope or {}), "group": code}, chance=group.get(f"chance{i}"))

    def slots(self, table, record, row, raw, target, reason=None):
        for column, code in raw.items():
            if not code:
                continue
            scope, chance = {}, None
            if re.fullmatch(r"prop\d+", column):
                n = column[4:]; p, lo, hi = "par"+n, "min"+n, "max"+n
            elif re.fullmatch(r"aprop\d+[ab]", column):
                n = column[5:]; p, lo, hi = "apar"+n, "amin"+n, "amax"+n
                if number(raw.get("add func")) == 2:
                    scope = {"itemsRequired": int(n[:-1])+1}
                elif number(raw.get("add func")) == 1:
                    scope = {"otherSetItem": True}
            elif re.fullmatch(r"[pf]code\d+[ab]?", column):
                p, lo, hi = column.replace("code", "param"), column.replace("code", "min"), column.replace("code", "max")
                scope = {"fullSet": True} if column[0] == "f" else {"itemsRequired": int(re.search(r"\d+", column)[0])}
            elif re.fullmatch(r"t1code\d+", column):
                p, lo, hi = column.replace("code", "param"), column.replace("code", "min"), column.replace("code", "max")
            elif re.fullmatch(r"mod\d+code", column):
                p, lo, hi = column.replace("code", "param"), column.replace("code", "min"), column.replace("code", "max")
            elif re.fullmatch(r"mod \d+( [bc])?", column):
                suffix = column[-2:] if column.endswith((" b", " c")) else ""
                prefix = column[:-2] if suffix else column
                p, lo, hi = prefix+" param"+suffix, prefix+" min"+suffix, prefix+" max"+suffix
                scope = {"output": suffix.strip().upper() or "A"}
                chance = raw.get(prefix+" chance"+suffix)
            elif re.fullmatch(r"(?:weapon|helm|shield)mod\d+code", column):
                p, lo, hi = column.replace("code", "param"), column.replace("code", "min"), column.replace("code", "max")
                scope = {"gemScope": re.match(r"\w+?(?=mod)", column)[0]}
            elif table == "propertygroups" and re.fullmatch(r"prop\d+", column):
                continue
            else:
                continue
            if table == "propertygroups":
                n = column[4:]; p, lo, hi = "parmin"+n, "modmin"+n, "modmax"+n
            parameter = raw.get(p)
            if table == "propertygroups" and raw.get("parmax"+n) and parameter != raw["parmax"+n]:
                parameter = f"{parameter}-{raw['parmax'+n]}" if parameter else raw["parmax"+n]
            self.add(table, record, row, column, code, parameter, raw.get(lo), raw.get(hi), target, scope, reason, chance)

    def collect(self):
        for table, file in [("uniqueitems", "uniques"), ("setitems", "sets"), ("sets", "sets"),
                            ("runes", "runewords"), ("cubemain", "cube-recipes")]:
            exported = self.document(f"keyed/{file}.json")
            if table == "setitems":
                exported = [item for s in exported for item in s["SetItems"]]
            by_id = collections.defaultdict(list)
            for item in exported:
                by_id[item["Index"]].append(item)
            for i, raw in enumerate(self.table(table)):
                record = raw.get("index") or raw.get("name") or raw.get("description") or f"row {i+2}"
                identity = i+1 if table == "cubemain" else record
                target = f"{table}:{identity}:{i}" if by_id[identity] else None
                if target:
                    item = by_id[identity].pop(0)
                    content = {k: v for k, v in item.items() if k != "SetItems"}
                    self.targets[target] = list(lines(content))
                    self.property_targets[target] = list(lines({k: v for k, v in content.items()
                        if k in ("Lines", "SetBonuses", "PartialBonuses", "FullBonuses")}))
                    if table == "cubemain":
                        self.output_targets[target] = {name: list(lines(output.get("Lines", []))) for name, output in item.get("Outputs", {}).items()}
                    self.levels[target] = 99 if table == "cubemain" else item.get("ItemLevel", 0)
                    self.raw_targets[target] = raw
                reason = None
                if not target:
                    # The validator does not reproduce the cube predicate language.
                    # Enabled omitted recipes require review; they are not passes.
                    reason = "No exported record; source eligibility/filter or import failure needs review"
                    if raw.get("disabled") == "1" and raw.get("spawnable") != "1":
                        reason = "Source explicitly disabled and unspawnable"
                    if table == "uniqueitems" and number(raw.get("lvl")) in (0, None):
                        reason = "Source level-zero/header row"
                    if table == "uniqueitems" and any(name.lower() in record.lower() for name in self.config["ignoredUniqueItems"]):
                        reason = "Configured unique-name exclusion"
                    if table == "runes" and raw.get("complete") != "1":
                        reason = "Incomplete runeword"
                    if table == "cubemain" and raw.get("enabled") != "1":
                        reason = "Disabled recipe"
                    elif table == "cubemain":
                        tokens = [token.strip().strip('"').lower() for n in range(1, 8)
                            for token in raw.get(f"input {n}", "").split(",")]
                        if set(tokens) & {code.lower() for code in self.config["blockedCubeInputCodes"]}:
                            reason = "Configured blocked cube input"
                        for rule in self.config["skippedCubeRecipes"]:
                            if all(number(raw.get(k)) == v for k, v in rule.items() if k in ("op", "param", "value")) \
                                and ("valueMin" not in rule or number(raw.get("value")) is not None and number(raw["value"]) >= rule["valueMin"]) \
                                and ("valueMax" not in rule or number(raw.get("value")) is not None and number(raw["value"]) <= rule["valueMax"]):
                                reason = "Configured cube skip rule"
                self.slots(table, record, i, raw, target, reason)
                if table == "runes" and target:
                    self.gem_contributions(raw, target, item)
        for table in ["magicprefix", "magicsuffix", "automagic"]:
            exported = self.document(f"keyed/{table}.json")
            cursor = 0
            for i, raw in enumerate(self.table(table)):
                target, reason = None, "Disabled affix or header"
                if table == "automagic":
                    buckets = [b for file in ["armors", "weapons"] for item in self.document(f"keyed/{file}.json")
                               for b in item.get("AutoMagicGroups", []) if b.get("Code") == raw.get("name")
                               and b.get("Level") == (number(raw.get("level")) or 0)
                               and b.get("RequiredLevel") == (number(raw.get("levelreq")) or 0)]
                    reason = "Automagic group skipped or has no exported base-item reference"
                    if number(raw.get("group")) in self.config["skippedAutoMagicGroups"]:
                        reason = "Configured automagic-group exclusion"
                    if buckets:
                        target = f"automagic:{i}"; self.targets[target] = list(lines(buckets)); self.levels[target] = number(raw.get("level")) or 0
                        self.host_targets[target] = [list(lines(b.get("Lines", []))) for b in buckets]
                    elif reason != "Configured automagic-group exclusion" and all(name + ".txt" in self.sources for name in ("armor", "weapons")):
                        hosts = [dict(Table=name + ".txt", Row=n+2, Code=r.get("code"))
                            for name in ("armor", "weapons") for n, r in enumerate(self.table(name))
                            if number(r.get("autoprefix")) == number(raw.get("group"))]
                        if not hosts:
                            reason = "Unreferenced automagic definition; no armor/weapon AutoPrefix host"
                elif raw.get("name") and raw.get("spawnable") == "1":
                    reason = "Missing affix row"
                    if cursor < len(exported) and exported[cursor]["NameKey"] == raw["name"]:
                        target = f"{table}:{i}"; self.targets[target] = list(lines(exported[cursor])); self.levels[target] = number(raw.get("level")) or 0
                    cursor += 1
                if target:
                    self.property_targets[target] = self.targets[target]
                before = len(self.occurrences)
                self.slots(table, raw.get("name", ""), i, raw, target, reason)
                if table == "automagic" and reason == "Unreferenced automagic definition; no armor/weapon AutoPrefix host":
                    for occurrence in self.occurrences[before:]:
                        occurrence["SourceHostReferences"] = []
        for table in ["gems", "propertygroups"]:
            for i, raw in enumerate(self.table(table)):
                self.slots(table, raw.get("code", ""), i, raw, None,
                    "No standalone keyed document; referenced occurrences are audited in their consuming record")
        for i, raw in enumerate(self.table("monprop")):
            for column, code in raw.items():
                match = re.fullmatch(r"prop(\d+)(.*)", column)
                if match and code:
                    suffix = match[1]+match[2]
                    self.add("monprop", raw.get("id", ""), i, column, code, raw.get("par"+suffix),
                        raw.get("min"+suffix), raw.get("max"+suffix), None,
                        reason="Health bundle carries MonProp identifier and table hash, not a keyed monster-property document")

    def ancestry(self, code, seen=None):
        seen = seen or set()
        if not code or code.lower() in seen:
            return set()
        seen.add(code.lower()); row = self.types.get(code.lower(), {})
        return {code.lower()} | self.ancestry(row.get("equiv1"), seen) | self.ancestry(row.get("equiv2"), seen)

    def gem_contributions(self, raw, target, exported):
        types = [self.ancestry(raw.get(f"itype{i}")) for i in range(1, 7) if raw.get(f"itype{i}")]
        scopes = []
        if any("shld" in t for t in types): scopes.append("shield")
        if any("weap" in t for t in types): scopes.append("weapon")
        if any("shld" not in t and "weap" not in t for t in types): scopes.append("helm")
        major_count = len({"shield" if "shld" in t else "weapon" if "weap" in t else "armor" for t in types})
        for i, gem in enumerate(self.table("gems")):
            if gem.get("code") not in [raw.get(f"rune{n}") for n in range(1, 7)]: continue
            # Duplicate rune codes contribute once per occupied rune slot.
            count = sum(gem.get("code") == raw.get(f"rune{n}") for n in range(1, 7))
            for scope in scopes:
                for repeat in range(count):
                    before = len(self.occurrences)
                    self.slots("gems", gem["code"], i, {k:v for k,v in gem.items() if k.startswith(scope+"mod")}, target)
                    for occurrence in self.occurrences[before:]:
                        occurrence["Scope"] = {"qualifier": ("armor" if scope == "helm" else scope)} if major_count > 1 else {}

    def expectation(self, o):
        code = o["Code"].lower(); prop = self.properties.get(code, {})
        funcs = [number(prop.get(f"func{i}")) for i in range(1, 8) if prop.get(f"func{i}")]
        o["Functions"] = funcs
        stat_names = [prop.get(f"stat{i}") for i in range(1, 8) if prop.get(f"stat{i}")]
        o["Stats"] = stat_names
        stat = self.synthetic.get(code) or self.stats.get((prop.get("stat1") or self.config["propertyStatOverrides"].get(code, code)).lower(), {})
        desc = number(stat.get("descfunc")); o["DescriptionFunction"] = desc
        low, high, param = o["Min"], o["Max"], o["Parameter"]
        values = endpoints(low, high)
        if code in self.groups: return "Group", [], None
        if code in self.config["ignoredPropertyCodes"]: return "Excluded", [], None
        if not funcs: return "Unknown", values, None
        # Explicitly understood engine functions. A new function must be given
        # semantics here instead of passing merely because its numbers match.
        if funcs[0] not in (1,2,5,6,7,8,10,11,12,13,14,15,17,19,20,21,22,23,24):
            return "Unknown", values, None
        if any(f not in (1,2,3,5,6,7,8,10,11,12,13,14,15,16,17,19,20,21,22,23,24) for f in funcs):
            return "Unknown", values, None
        key = stat.get("descstrneg") if high is not None and high < 0 and stat.get("descstrneg") else stat.get("descstrpos")
        if funcs[0] == 17:
            try:
                values, o["ValueSource"] = function17_values(param, low, high)
            except ValueError:
                o["Reason"] = "Function-17 parameter is not an integer"
                return "Unsupported", [], key
            o["StatValues"] = values
            o["StatOperation"] = "Replace" if number(prop.get("set1")) == 1 else "Add"
            low, high = values[0], values[-1]
            key = stat.get("descstrneg") if high < 0 and stat.get("descstrneg") else stat.get("descstrpos")
        template = self.strings.get(key)
        if funcs[0] == 17 and template is None:
            o["Reason"] = "Missing manifested enUS template for function-17 description"
            return "Unsupported", values, key
        if (funcs[0] != 17 or funcs == [17]) and desc in (1,2,3,4,12,19,20) and template is not None and not placeholders(template):
            o["Presentation"] = "ValueLess"
            # Some ordinary boolean lines retain unused source endpoint args.
            # They may be omitted; never manufacture an argument from a param.
            o["UnusedSourceArgs"] = endpoints(o["Min"], o["Max"]) if funcs[0] != 17 else []
            if funcs == [17] and desc == 3 and number(stat.get("descval")) == 2 and o["ValueSource"] == "Endpoints":
                # Engine live-stat label (e.g. lab_kill_display): descval 2 appends the
                # player's current stat after the slotless text, so the configured
                # endpoint is a placeholder that may ride along unused, never a count.
                o["Presentation"] = "LiveStatLabel"
                o["UnusedSourceArgs"] = o["StatValues"]
            if desc == 20:
                o["UnusedSourceArgs"] = [-v for v in o["UnusedSourceArgs"]]
            return "ValueLess", [], key
        if not key and desc is None and stat and (not values or prop.get("stat1") not in ("coldlength", "poisonlength")):
            # No source tooltip and no independently modeled duration value.
            return "NonDisplay", [], None
        if prop.get("stat1") in ("coldlength", "poisonlength"):
            return "Duration", [v/25 for v in values], "StrSkill13" if prop["stat1"] == "coldlength" else "StrSkill20"
        if code == "dur" and o["Target"] and o["Table"] in ("uniqueitems.txt", "setitems.txt"):
            raw = self.raw_targets[o["Target"]]
            base = self.equipment.get((raw.get("code") or raw.get("item") or "").lower(), {})
            durability = number(base.get("durability"))
            if not durability or base.get("nodurability") == "1": return "Unsupported", values, None
            a = durability+(low if low is not None else high or 0)
            b = durability+(high if high is not None else low or 0)
            return "Durability", [a,b,a,b] if a!=b else [a,a], "strDurabilityRolls" if a!=b else "strDurability"
        if code == "ease":
            a = low if low is not None else high
            b = high if high is not None else a
            if a is not None and a <= 0 and b <= 0:
                values = endpoints(abs(b), abs(a)); key = stat.get("descstrneg")
        if desc == 29:
            return "Roll", values or ([number(param)] if param else []), "strSocketedCount"
        if funcs[0] == 17:
            if desc == 11:
                seconds = number(param)
                if seconds is None or seconds <= 0:
                    return "Parameter", [0], key
                repair_key = key if seconds == 1 else stat.get("descstr2") or "ModStre9u"
                if placeholders(self.strings.get(repair_key, "")) != ["d"] * (1 if seconds == 1 else 2):
                    o["Reason"] = "Missing or unmodeled function-17 repair template"
                    return "Unsupported", values, repair_key
                return "Parameter", [1] if seconds == 1 else [1, seconds], repair_key
            if desc == 19 and ("perlevel" in (prop.get("stat1") or "") or "perlvl" in (prop.get("stat1") or "")):
                if placeholders(template) not in (["d"], ["u"], ["f"]):
                    return "Unsupported", values, key
                divisor = 2 ** (number(stat.get("op param")) or 0)
                o["ExpectedPerLevel"] = True
                return "Scaling", [v/divisor for v in values], key
            # A one-slot numeric template accepts a fixed contribution or both
            # endpoints of a roll; counters are deltas, not displayed totals.
            if funcs != [17] or desc not in (1,2,3,4,5,12,19,20) or template is None or placeholders(template) not in (["d"], ["u"], ["f"]):
                return "Unsupported", values, key
            if desc == 5:
                values = list(dict.fromkeys(math.trunc(v*100/128) for v in values))
            if desc == 20:
                values = [-v for v in values]
            return "Function17", values, key
        if funcs[0] == 11:
            if desc == 15 and high and low is not None:
                skill = self.skills.get((param or "").lower(), {})
                return "Independent", [low, high, skill.get("namekey", param or "")], key
            return "Unsupported", values, key
        if funcs[0] == 19:
            if high is None or low is None: return "Unsupported", [], key
            charges = min(255, 5 if low == 0 else abs(low) + abs(low)*self.levels.get(o["Target"], 0)//8 if low < 0 else low)
            return "Independent", [high, self.skills.get((param or "").lower(), {}).get("namekey", param or ""), charges, charges], "strSkillCharges"
        if funcs[0] == 12:
            return "Selection", [number(param) if param else number(prop.get("val1")) or 0], None
        if desc == 30:
            if low is None or high is None or not param: return "Unsupported", values, key
            frames = int(param)
            return "Components", [math.ceil(low*frames/256), math.ceil(high*frames/256), frames/25], "strModPoisonDamageRange"
        if funcs[0] == 15:
            if funcs not in ([15,16], [15,16,17], [15,16,15,16,15,16,17]): return "Unsupported", values, key
            # Minimum/maximum damage are two slots, even when both equal 7.
            components = [low, high] if desc == 31 and low is not None and high is not None else values
            if prop.get("stat1") == "coldmindam" and param:
                o["AdditionalExpected"] = dict(key="StrSkill13", args=[int(param)/25])
            return "Components" if desc == 31 else "Roll", components, key
        if desc == 13:
            cs = next((r for r in self.charstats if r.get("class", "").lower().startswith(code)), None)
            if cs: key = cs.get("strallskills") or key
        if desc == 14:
            if param and re.fullmatch(r"\d+-\d+",param):
                a,b = map(int,param.split("-"))
                o["ExpectedSelection"] = dict(stat=prop["stat1"], min=a, max=b)
            # Exact tab/class key resolution is checked by the fixed-value fixtures;
            # preserve endpoints even when its parameter is a class/tab selector.
            return "TabRoll", values, None
        if desc in (16,28):
            values += [self.skills.get((param or "").lower(), {}).get("namekey", param or "")]
        if desc == 27:
            skill = self.skills.get((param or "").lower(), {})
            values = [low if low is not None else high, high if high is not None else low, skill.get("namekey", param or "")]
            key = "strSkillRandomFromSkillClass" if skill.get("charclass") else "strSkillRandomFromSkill"
        if desc == 5:
            values = list(dict.fromkeys(math.trunc(v*100/128) for v in values))
            o["Transformation"] = "Engine integer scaling by 100/128, truncating toward zero"
        if desc == 20: values = [-v for v in values]
        if desc in (6,7,8,9,17,18,21,22) or desc not in (1,2,3,4,5,12,13,14,16,19,20,23,27,28,29,30,31,None):
            return "Unsupported", values, key
        if len(set(stat_names)) > 1 and code not in self.synthetic and code != "oskill":
            return "MultiStat", values, key
        return "Roll", values, key

    def scoped_candidates(self, o, candidates):
        scope = o["Scope"]
        if scope.get("group"):
            parents = [l for l in candidates if l.get("code", "").lower() == scope["group"].lower()]
            candidates = [l for p in parents for l in p.get("children", [])]
            if o["Chance"] is not None and o["Chance"] > 0:
                candidates = [l for l in candidates if l.get("chance") == o["Chance"]]
        for field in ("itemsRequired", "fullSet", "qualifier"):
            candidates = [l for l in candidates if l.get(field) == scope.get(field)]
        return candidates

    def evaluate(self):
        # Expand configured shorthand without calling the production implementation.
        expanded = []
        for o in self.occurrences:
            components = self.config["propertyCodeExpansions"].get(o["Code"].lower(), [o["Code"]])
            for code in components:
                expanded.append({**o, "SourceCode": o["Code"], "Code": code})
        self.occurrences = expanded
        identities = collections.defaultdict(list)
        for o in expanded:
            semantic, expected, key = self.expectation(o)
            o.update(Semantic=semantic, Expected=expected, ExpectedKey=key)
            identities[(o["Target"], o["Code"].lower(), o["Parameter"], o["Chance"], json.dumps(o["Scope"], sort_keys=True))].append(o)
        for group in identities.values():
            first = group[0]
            if first["Target"] and first["Target"].split(":")[0] in ["uniqueitems","setitems","runes"] and len(group)>1:
                if first["Semantic"] in ("Roll", "Components", "Function17", "Scaling", "ValueLess") and first["Code"].lower() not in self.config["propertyMergeBlocklist"] and not first["Scope"].get("group") and first.get("StatOperation") != "Replace":
                    low = sum(o["Min"] if o["Min"] is not None else o["Max"] or 0 for o in group)
                    high = sum(o["Max"] if o["Max"] is not None else o["Min"] or 0 for o in group)
                    for o in group:
                        merged = {**o, "Min":low,"Max":high}
                        if first["Functions"][0] == 17:
                            # Sum independently evaluated engine contributions,
                            # not ignored endpoint columns behind a parameter.
                            merged["Min"] = sum(v["StatValues"][0] for v in group)
                            merged["Max"] = sum(v["StatValues"][-1] for v in group)
                            merged["Parameter"] = str(merged["Min"]) if first["ValueSource"] == "Parameter" else None
                        _, o["Expected"], o["ExpectedKey"] = self.expectation(merged)
                        if "UnusedSourceArgs" in merged:
                            o["UnusedSourceArgs"] = merged["UnusedSourceArgs"]
                        o["Transformation"] = "Additive roll sum within matching activation scope"
        # Component pairing is an independent expected transformation. Accept
        # correctly retained separate component lines as well as a composite.
        pairs = [("dmg-min","dmg-max",None,"strModMinDamageRange","strDamageMergedRange"),
            ("fire-min","fire-max",None,"strModFireDamageRange","strFireDamageRolls"),
            ("cold-min","cold-max","cold-len","strModColdDamageRange","strColdDamageRolls"),
            ("ltng-min","ltng-max",None,"strModLightningDamageRange","strLightningDamageRolls"),
            ("pois-min","pois-max","pois-len","strModPoisonDamageRange","strPoisonDamageRolls")]
        for o in expanded:
            if not o["Target"] or o["Scope"].get("group"): continue
            for mn,mx,duration,fixed_key,roll_key in pairs:
                if o["Code"].lower() not in (mn,mx,duration): continue
                same = [v for v in expanded if v["Target"]==o["Target"] and v["Scope"]==o["Scope"]]
                left = next((v for v in same if v["Code"].lower()==mn),None)
                right = next((v for v in same if v["Code"].lower()==mx),None)
                length = next((v for v in same if duration and v["Code"].lower()==duration),None)
                if not left or not right or not left["Expected"] or not right["Expected"]: continue
                a,b = left["Expected"][:2],right["Expected"][:2]
                if len(a)==1: a=a*2
                if len(b)==1: b=b*2
                ranged = a[0]!=a[1] or b[0]!=b[1]
                expected = a+b if ranged else [a[0],b[0]]
                if mn=="pois-min":
                    if not length or length["Min"]!=length["Max"]: continue
                    frames = length["Min"]
                    expected = [math.ceil(v*frames/256) for v in expected]+[frames/25]
                elif o["Code"].lower()==duration: continue
                o["Alternative"] = dict(key=roll_key if ranged else fixed_key,args=expected)
        for o in expanded:
            target, semantic, expected, key = o["Target"], o["Semantic"], o["Expected"], o["ExpectedKey"]
            o["Status"] = "Unevaluated"
            if semantic == "Excluded": o["Status"] = "Excluded"; o["Reason"] = "Configured ignored property"
            elif not target and o["Reason"] == "Unreferenced automagic definition; no armor/weapon AutoPrefix host":
                o["Status"] = "UnreferencedAutomagicDefinition"
            elif not target: o["Status"] = "NotExported"
            elif semantic == "NonDisplay":
                o["Status"] = "IntentionalNonDisplayStat"; o["Reason"] = "No source description template or independently modeled display value"
            elif semantic in ("Unknown", "Unsupported", "MultiStat"):
                o["Status"] = "Unsupported"; o["Reason"] = o["Reason"] or "Mapping semantics require independent fixture or additional function support"
            elif semantic == "Group":
                parents = [l for l in self.targets[target] if l.get("code", "").lower()==o["Code"].lower() and l.get("children")]
                o["Status"] = "Verified" if parents else "MissingProperty"
            else:
                candidates = self.property_targets[target] if o["Functions"][0] == 17 else self.targets[target]
                scope = o["Scope"]
                if scope.get("output"):
                    candidates = self.output_targets.get(target, {}).get(scope["output"], [])
                scoped_lines = self.scoped_candidates(o, candidates)
                candidates = scoped_lines
                if key: candidates = [l for l in candidates if l.get("key")==key]
                if semantic == "Selection":
                    candidates = [l for l in candidates if l.get("selection")==dict(stat=o["Stats"][0],
                        **({"min":o["Min"]} if o["Min"] is not None else {}),
                        **({"max":o["Max"]} if o["Max"] is not None else {}))]
                if "ExpectedSelection" in o:
                    candidates = [l for l in candidates if l.get("selection")==o["ExpectedSelection"]]
                def matches(line):
                    args = line.get("args", [])
                    if o["Functions"][0] == 17 and any(type(e) in (int, float) and type(a) not in (int, float) for a, e in zip(args, expected)):
                        return False
                    if "ExpectedPerLevel" in o and line.get("perLevel") != o["ExpectedPerLevel"]:
                        return False
                    if semantic == "ValueLess":
                        return args == [] or args == o["UnusedSourceArgs"]
                    if semantic == "Function17":
                        return len(args) == len(expected) and all(type(a) in (int, float) and a == b for a, b in zip(args, expected))
                    if semantic in ("Components", "Independent"):
                        return args == expected
                    if args[:len(expected)] != expected:
                        return False
                    # A numeric roll may be followed by a skill/class/monster
                    # key, but never by an invented third numeric endpoint.
                    if expected and all(type(v) in (int, float) for v in expected):
                        return len([v for v in args if type(v) in (int, float)]) == len(expected)
                    return True
                found = [l for l in candidates if matches(l)]
                alternative = o.get("Alternative")
                if alternative:
                    found += [l for l in scoped_lines if l.get("key")==alternative["key"] and l.get("args")==alternative["args"]
                        and all(l.get(f)==scope.get(f) for f in ["itemsRequired","fullSet","qualifier"])]
                if found and (expected or semantic == "ValueLess"):
                    o["Status"] = "Verified"; o["Actual"] = found[0]
                    hosts = self.host_targets.get(target, [])
                    if hosts and not all(any((not key or l.get("key") == key) and matches(l) for l in self.scoped_candidates(o, host)) for host in hosts):
                        o["Status"] = "MissingProperty"; o["Reason"] = "Expected property missing or incorrect on an automagic host"
                    additional = o.get("AdditionalExpected")
                    if additional and not any(l.get("key") == additional["key"] and l.get("args") == additional["args"]
                        for l in scoped_lines):
                        o["Status"] = "MissingProperty"; o["Reason"] = "Missing duration from direct cold-damage parameter"
                elif key and not expected and any(p in ("d", "u", "f") for p in placeholders(self.strings.get(key, ""))) and candidates:
                    o["Status"] = "IncorrectValues"; o["Actual"] = candidates
                    o["Reason"] = "Numeric template has no independently established source value; parameter is not a function-1 fallback"
                elif key and not expected and any(p in ("d", "u", "f") for p in placeholders(self.strings.get(key, ""))):
                    # The exporter omits such a line instead of shipping a raw placeholder.
                    # The source gap stays unresolved; an absent line is not a pass.
                    o["Status"] = "Unevaluated"
                    o["Reason"] = "Numeric template has no source value; line omitted, source data needs a value"
                elif (not key and semantic != "Selection") or not expected and semantic != "ValueLess":
                    o["Status"] = "Unevaluated"; o["Reason"] = "No independently established rendered value/key"
                else:
                    o["Status"] = "MissingProperty" if not candidates else "IncorrectValues"
                    o["Actual"] = candidates
                    if len(expected)==2 and expected[0]!=expected[1] and any(l.get("args")==expected[:1] for l in candidates):
                        o["Status"] = "CollapsedRange"
        counts = dict(collections.Counter(o["Status"] for o in expanded))
        unresolved_eligibility = sum(o["Status"] == "NotExported" and o["Reason"] in (
            "No exported record; source eligibility/filter or import failure needs review", "Missing affix row",
            "Automagic group skipped or has no exported base-item reference") for o in expanded)
        return dict(SchemaVersion=1, ManifestSucceeded=self.manifest["Succeeded"],
            ManifestComplete=self.manifest["Complete"], SourceHashes=self.source_hashes, ConfigHashes=self.config_hashes,
            TableCoverage={name + ".txt": dict(Available=name + ".txt" in self.sources, Rows=len(rows)) for name, rows in self.tables.items()},
            UnresolvedEligibility=unresolved_eligibility,
            Counts=counts, Occurrences=expanded,
            Function17Coverage=dict(collections.Counter(o["Status"] for o in expanded if 17 in o["Functions"])),
            Limitations=["Source hashes describe inputs at audit time; manifest does not attest their export-time bytes.",
                "NotExported includes deliberate filters, unreferenced definitions and potentially omitted eligible rows; review Reason.",
                "Value-less verification proves the tooltip's key/scope, not a hidden rate or live activation behavior.",
                "IntentionalNonDisplayStat and UnreferencedAutomagicDefinition are omissions, not correctness passes.",
                "Unsupported and Unevaluated are not correctness passes."])


def exit_code(report):
    if not report["ManifestSucceeded"] or not report["ManifestComplete"] \
        or any(report["Counts"].get(s,0) for s in ["MissingProperty","IncorrectValues","CollapsedRange"]):
        return 1
    return 2 if report["UnresolvedEligibility"] or any(not t["Available"] for t in report["TableCoverage"].values()) \
        or any(report["Counts"].get(s,0) for s in ["Unsupported","Unevaluated"]) else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--export", required=True)
    parser.add_argument("--config", default=str(Path(__file__).resolve().parents[1]/"D2RMultiExport.Lib/Config/export-config.json"))
    args = parser.parse_args()
    audit = Audit(args.export, args.config)
    audit.collect()
    report = audit.evaluate()
    json.dump(report, sys.stdout, indent=2, ensure_ascii=True)
    sys.stdout.write("\n")
    return exit_code(report)


if __name__ == "__main__":
    sys.exit(main())
