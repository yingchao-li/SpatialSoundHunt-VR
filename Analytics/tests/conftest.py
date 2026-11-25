import sys
from pathlib import Path

ANALYTICS_DIR = Path(__file__).resolve().parent.parent
for p in (str(ANALYTICS_DIR), str(Path(__file__).resolve().parent)):
    if p not in sys.path:
        sys.path.insert(0, p)

import pytest  # noqa: E402

from synthetic import standard_session  # noqa: E402


@pytest.fixture()
def standard_csv(tmp_path):
    """SYNTHETIC session (see tests/synthetic.py) - never a real experiment result."""
    return standard_session().write(tmp_path / "in" / "session_standard.csv")
