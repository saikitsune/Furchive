"""Regression tests for the native acceptance transcript guard."""
import unittest
from native_check import validate_tests

class ReceiptTests(unittest.TestCase):
    def test_real_rust_success_shape(self):
        validate_tests('running 2 tests\ntest tests::a ... ok\ntest tests::b ... ok\ntest result: ok. 2 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.1s\n', ['tests::a', 'tests::b'])
    def test_zero_tests_is_not_success(self):
        with self.assertRaises(ValueError):
            validate_tests('test result: ok. 0 passed; 0 failed; 0 ignored\n', ['tests::a'])
    def test_duplicate_or_ignored_or_diagnostic_is_not_success(self):
        for text in ['test tests::a ... ok\ntest tests::a ... ok\ntest result: ok. 1 passed; 0 failed; 0 ignored\n',
                     'test tests::a ... ignored\ntest result: ok. 0 passed; 0 failed; 1 ignored\n',
                     'warning: unexpected\ntest tests::a ... ok\ntest result: ok. 1 passed; 0 failed; 0 ignored\n']:
            with self.assertRaises(ValueError):
                validate_tests(text, ['tests::a'])

if __name__ == '__main__':
    unittest.main()
