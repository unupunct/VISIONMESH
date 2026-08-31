"""Keeps the test stub honest against the server that actually exists.

Mocked integration tests have one classic failure: they prove the integration handles the payload
the test author imagined, which drifts from the payload the server sends. The camera list is
built in C#, in `HomeAssistantEndpoints.cs`, so the field names live in a different language from
the code consuming them and nothing normally connects the two.

This reads that endpoint and checks the stub still matches it. It is a source check rather than a
live call on purpose: it runs in CI with no server, and it fails at the moment the endpoint
changes rather than the moment a user notices an entity has gone blank.
"""

from __future__ import annotations

from pathlib import Path
import re

import pytest

from .payloads import make_camera

REPOSITORY = Path(__file__).resolve().parents[2]
ENDPOINT = REPOSITORY / "server" / "VisionMesh.Api" / "Endpoints" / "HomeAssistantEndpoints.cs"


def _entities_projection() -> str:
    source = ENDPOINT.read_text(encoding="utf-8")
    start = source.index('group.MapGet("/entities"')
    end = source.index(".RequireViewer()", start)
    return source[start:end]


def test_the_endpoint_source_is_where_we_think_it_is() -> None:
    assert ENDPOINT.exists(), f"{ENDPOINT} is missing — this test can no longer check anything."


@pytest.mark.parametrize(
    "field",
    ["id", "name", "uniqueId", "sourceKind", "state", "ptzSupported", "streamUrl", "snapshotUrl", "health"],
)
def test_every_field_the_stub_returns_is_produced_by_the_server(field: str) -> None:
    """If the server stops sending a field, the stub must stop pretending it does."""
    projection = _entities_projection()

    # The C# projection uses shorthand members (`camera.Id`) and explicit ones (`streamUrl = ...`),
    # and ASP.NET serialises both as camelCase.
    shorthand = f"camera.{field[0].upper()}{field[1:]}"
    explicit = f"{field} ="

    assert shorthand in projection or explicit in projection, (
        f"The stub returns '{field}', but the /entities endpoint no longer produces it. "
        f"Either the endpoint changed and the integration needs updating, or the stub is lying."
    )


def test_the_supports_flags_match() -> None:
    """These decide which entities are created at all, so a rename silently removes entities."""
    projection = _entities_projection()
    for flag in make_camera()["supports"]:
        assert re.search(rf"\b{flag}\s*=", projection), (
            f"The stub advertises supports.{flag}, which the server no longer sends."
        )


def test_unique_ids_are_built_the_same_way_on_both_sides() -> None:
    """A change of prefix here renames every entity in every user's Home Assistant."""
    projection = _entities_projection()
    assert 'uniqueId = $"visionmesh_{camera.Id}"' in projection, (
        "The server's unique id format changed. Entity ids are derived from it, so changing it "
        "silently orphans every existing entity and every automation pointing at one."
    )
    assert make_camera()["uniqueId"] == "visionmesh_cam_abc123"
