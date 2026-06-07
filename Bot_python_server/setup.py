from setuptools import setup, find_packages

setup(
    name="runspace",
    version="0.1.0",
    description="Official Python SDK for RunSpace bots",
    packages=find_packages(),
    python_requires=">=3.8",
    install_requires=[
        "requests>=2.28.0",
    ],
    classifiers=[
        "Programming Language :: Python :: 3",
        "License :: OSI Approved :: MIT License",
    ],
)
